using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using Dapper;
using Check_Sheet_Online;

namespace TabletCheckIn.Repositories
{
    // Thrown when a task was modified by someone else (row_version mismatch).
    // The controller turns this into HTTP 409 so the page can refresh.
    public class ConcurrencyException : Exception
    {
        public ConcurrencyException(string msg) : base(msg) { }
    }

    public class KanbanRepository
    {
        // -------- small helpers --------
        private static string NewId() { return Guid.NewGuid().ToString("N"); }            // 32 hex chars
        private static string NowIso() { return DateTime.Now.ToString("yyyy-MM-ddTHH:mm:sszzz"); }

        // =========================================================
        //  READ — whole board + recent activity.
        //  Auto-creates a default board the first time it's empty.
        // =========================================================
        public object GetBoard(string actor, string actorName)
        {
            using (var conn = PostgreSqlDbConnection.GetConnection())
            {
                conn.Open();

                bool empty = conn.ExecuteScalar<long>("SELECT COUNT(*) FROM kanban.tm_project") == 0;
                if (empty)
                {
                    using (var tx = conn.BeginTransaction())
                    {
                        SeedDefaultProject(conn, tx, actor, actorName);
                        tx.Commit();
                    }
                }

                var board = ReadBoard(conn, null);
                var activities = conn.Query<ActivityRow>(
                    "SELECT id, project_id, actor, actor_name, action, entity_type, entity_id, summary, created_at " +
                    "FROM kanban.td_activity ORDER BY id DESC LIMIT 100").ToList();
                long lastId = activities.Count > 0 ? activities.Max(a => a.id) : 0;

                return new
                {
                    projects = board.Item1,
                    tasks = board.Item2,
                    activities = activities.Select(ToActivityDto).ToList(),
                    lastActivityId = lastId
                };
            }
        }

        // =========================================================
        //  CHANGES — activities newer than the id the page last saw.
        // =========================================================
        public object GetChanges(long sinceId)
        {
            using (var conn = PostgreSqlDbConnection.GetConnection())
            {
                var rows = conn.Query<ActivityRow>(
                    "SELECT id, project_id, actor, actor_name, action, entity_type, entity_id, summary, created_at " +
                    "FROM kanban.td_activity WHERE id > @s ORDER BY id ASC LIMIT 200", new { s = sinceId }).ToList();
                long lastId = rows.Count > 0 ? rows.Max(a => a.id) : sinceId;
                return new { activities = rows.Select(ToActivityDto).ToList(), lastActivityId = lastId };
            }
        }

        // =========================================================
        //  APPLY — one granular operation, transactional, logged.
        //  Returns { ok, activity, result }.
        // =========================================================
        public object Apply(string actor, string actorName, OpRequest op)
        {
            if (op == null || string.IsNullOrWhiteSpace(op.op))
                throw new ArgumentException("missing op");

            using (var conn = PostgreSqlDbConnection.GetConnection())
            {
                conn.Open();
                using (var tx = conn.BeginTransaction())
                {
                    object activity = null;
                    object result = null;

                    switch (op.op)
                    {
                        // ---------------- PROJECTS ----------------
                        case "saveProject":
                        {
                            bool exists = conn.ExecuteScalar<bool>(
                                "SELECT EXISTS(SELECT 1 FROM kanban.tm_project WHERE id=@id)", new { id = op.id }, tx);
                            conn.Execute(
                                "INSERT INTO kanban.tm_project (id,name,emoji,color,sort_order) VALUES (@id,@name,@emoji,@color,@sort) " +
                                "ON CONFLICT (id) DO UPDATE SET name=@name, emoji=@emoji, color=@color",
                                new { id = op.id, name = op.name ?? "", emoji = op.emoji, color = op.color, sort = op.sortOrder ?? 0 }, tx);

                            if (!exists && op.createDefaults == true)
                            {
                                var defs = InsertDefaults(conn, tx, op.id);
                                result = defs;
                            }
                            activity = Log(conn, tx, op.id, actor, actorName, "project", "project", op.id,
                                (exists ? "แก้ไขโปรเจค «" : "สร้างโปรเจค «") + (op.name ?? "") + "»");
                            break;
                        }
                        case "deleteProject":
                        {
                            var nm = conn.ExecuteScalar<string>("SELECT name FROM kanban.tm_project WHERE id=@id", new { id = op.id }, tx);
                            conn.Execute("DELETE FROM kanban.tm_project WHERE id=@id", new { id = op.id }, tx);
                            activity = Log(conn, tx, op.id, actor, actorName, "delete", "project", op.id,
                                "ลบโปรเจค «" + (nm ?? "") + "»");
                            break;
                        }

                        // ---------------- COLUMNS ----------------
                        case "saveColumn":
                        {
                            bool exists = conn.ExecuteScalar<bool>(
                                "SELECT EXISTS(SELECT 1 FROM kanban.tm_column WHERE id=@id)", new { id = op.id }, tx);
                            conn.Execute(
                                "INSERT INTO kanban.tm_column (id,project_id,name,color,sort_order) VALUES (@id,@pid,@name,@color,@sort) " +
                                "ON CONFLICT (id) DO UPDATE SET name=@name, color=@color",
                                new { id = op.id, pid = op.projectId, name = op.name ?? "", color = op.color, sort = op.sortOrder ?? 0 }, tx);
                            activity = Log(conn, tx, op.projectId, actor, actorName, "column", "column", op.id,
                                (exists ? "แก้ไขคอลัมน์ «" : "เพิ่มคอลัมน์ «") + (op.name ?? "") + "»");
                            break;
                        }
                        case "deleteColumn":
                        {
                            var nm = conn.ExecuteScalar<string>("SELECT name FROM kanban.tm_column WHERE id=@id", new { id = op.id }, tx);
                            var pid = conn.ExecuteScalar<string>("SELECT project_id FROM kanban.tm_column WHERE id=@id", new { id = op.id }, tx);
                            conn.Execute("UPDATE kanban.td_task SET column_id=@fb WHERE column_id=@id",
                                new { fb = op.fallbackId, id = op.id }, tx);
                            conn.Execute("DELETE FROM kanban.tm_column WHERE id=@id", new { id = op.id }, tx);
                            activity = Log(conn, tx, pid, actor, actorName, "delete", "column", op.id,
                                "ลบคอลัมน์ «" + (nm ?? "") + "»");
                            break;
                        }

                        // ---------------- CATEGORIES ----------------
                        case "saveCategory":
                        {
                            bool exists = conn.ExecuteScalar<bool>(
                                "SELECT EXISTS(SELECT 1 FROM kanban.tm_category WHERE id=@id)", new { id = op.id }, tx);
                            conn.Execute(
                                "INSERT INTO kanban.tm_category (id,project_id,name,color,sort_order) VALUES (@id,@pid,@name,@color,@sort) " +
                                "ON CONFLICT (id) DO UPDATE SET name=@name, color=@color",
                                new { id = op.id, pid = op.projectId, name = op.name ?? "", color = op.color, sort = op.sortOrder ?? 0 }, tx);
                            activity = Log(conn, tx, op.projectId, actor, actorName, "category", "category", op.id,
                                (exists ? "แก้ไขหมวดหมู่ «" : "เพิ่มหมวดหมู่ «") + (op.name ?? "") + "»");
                            break;
                        }
                        case "deleteCategory":
                        {
                            var nm = conn.ExecuteScalar<string>("SELECT name FROM kanban.tm_category WHERE id=@id", new { id = op.id }, tx);
                            var pid = conn.ExecuteScalar<string>("SELECT project_id FROM kanban.tm_category WHERE id=@id", new { id = op.id }, tx);
                            conn.Execute("UPDATE kanban.td_task SET category_id=@fb WHERE category_id=@id",
                                new { fb = op.fallbackId, id = op.id }, tx);
                            conn.Execute("DELETE FROM kanban.tm_category WHERE id=@id", new { id = op.id }, tx);
                            activity = Log(conn, tx, pid, actor, actorName, "delete", "category", op.id,
                                "ลบหมวดหมู่ «" + (nm ?? "") + "»");
                            break;
                        }

                        // ---------------- TASKS ----------------
                        case "createTask":
                        {
                            string now = NowIso();
                            conn.Execute(
                                "INSERT INTO kanban.td_task (id,project_id,column_id,category_id,title,description,priority,due,sort_order,row_version,created_by,created_at,updated_by,updated_at) " +
                                "VALUES (@id,@pid,@col,@cat,@title,'',@prio,@due,@sort,1,@by,@now,@by,@now)",
                                new { id = op.id, pid = op.projectId, col = op.columnId, cat = op.categoryId,
                                      title = op.text ?? "", prio = op.priority, due = op.due ?? "",
                                      sort = op.order ?? 0, by = actorName, now = now }, tx);
                            result = new { rowVersion = 1, createdAt = now };
                            activity = Log(conn, tx, op.projectId, actor, actorName, "create", "task", op.id,
                                "เพิ่มงาน «" + (op.text ?? "") + "»");
                            break;
                        }
                        case "updateTask":
                        {
                            string col = FieldToColumn(op.field);
                            if (col == null) throw new ArgumentException("bad field: " + op.field);

                            string title = conn.ExecuteScalar<string>(
                                "SELECT title FROM kanban.td_task WHERE id=@id", new { id = op.id }, tx);
                            string pid = conn.ExecuteScalar<string>(
                                "SELECT project_id FROM kanban.td_task WHERE id=@id", new { id = op.id }, tx);

                            string now = NowIso();
                            int affected = conn.Execute(
                                "UPDATE kanban.td_task SET " + col + "=@val, row_version=row_version+1, updated_by=@by, updated_at=@now " +
                                "WHERE id=@id AND row_version=@rv",
                                new { val = op.value, by = actorName, now = now, id = op.id, rv = op.rowVersion ?? 1 }, tx);

                            if (affected == 0)
                            {
                                bool stillThere = conn.ExecuteScalar<bool>(
                                    "SELECT EXISTS(SELECT 1 FROM kanban.td_task WHERE id=@id)", new { id = op.id }, tx);
                                throw new ConcurrencyException(stillThere ? "row_version mismatch" : "task deleted");
                            }

                            int newRv = conn.ExecuteScalar<int>("SELECT row_version FROM kanban.td_task WHERE id=@id", new { id = op.id }, tx);
                            result = new { rowVersion = newRv, updatedAt = now };

                            string act = op.field == "status" ? "move" : "update";
                            activity = Log(conn, tx, pid, actor, actorName, act, "task", op.id,
                                FieldSummary(conn, tx, op.field, op.value, title));
                            break;
                        }
                        case "moveTask":
                        {
                            string title = conn.ExecuteScalar<string>(
                                "SELECT title FROM kanban.td_task WHERE id=@id", new { id = op.id }, tx);
                            string pid = conn.ExecuteScalar<string>(
                                "SELECT project_id FROM kanban.td_task WHERE id=@id", new { id = op.id }, tx);

                            conn.Execute("UPDATE kanban.td_task SET column_id=@col, updated_by=@by, updated_at=@now WHERE id=@id",
                                new { col = op.columnId, by = actorName, now = NowIso(), id = op.id }, tx);

                            if (op.orderedIds != null)
                                for (int i = 0; i < op.orderedIds.Count; i++)
                                    conn.Execute("UPDATE kanban.td_task SET sort_order=@o WHERE id=@id",
                                        new { o = i, id = op.orderedIds[i] }, tx);

                            string colName = conn.ExecuteScalar<string>(
                                "SELECT name FROM kanban.tm_column WHERE id=@id", new { id = op.columnId }, tx);
                            activity = Log(conn, tx, pid, actor, actorName, "move", "task", op.id,
                                "ย้ายงาน «" + (title ?? "") + "» ไป «" + (colName ?? "") + "»");
                            break;
                        }
                        case "deleteTask":
                        {
                            string title = conn.ExecuteScalar<string>(
                                "SELECT title FROM kanban.td_task WHERE id=@id", new { id = op.id }, tx);
                            string pid = conn.ExecuteScalar<string>(
                                "SELECT project_id FROM kanban.td_task WHERE id=@id", new { id = op.id }, tx);
                            conn.Execute("DELETE FROM kanban.td_task WHERE id=@id", new { id = op.id }, tx);
                            activity = Log(conn, tx, pid, actor, actorName, "delete", "task", op.id,
                                "ลบงาน «" + (title ?? "") + "»");
                            break;
                        }

                        // ---------------- SUBTASKS ----------------
                        case "addSubtask":
                        {
                            int nextOrder = conn.ExecuteScalar<int>(
                                "SELECT COALESCE(MAX(sort_order)+1,0) FROM kanban.td_subtask WHERE task_id=@t", new { t = op.taskId }, tx);
                            conn.Execute("INSERT INTO kanban.td_subtask (id,task_id,label,is_done,sort_order) VALUES (@id,@t,@l,FALSE,@o)",
                                new { id = op.id, t = op.taskId, l = op.text ?? "", o = nextOrder }, tx);
                            activity = Log(conn, tx, TaskProject(conn, tx, op.taskId), actor, actorName, "update", "subtask", op.taskId,
                                "เพิ่มงานย่อย «" + (op.text ?? "") + "»");
                            break;
                        }
                        case "toggleSubtask":
                        {
                            conn.Execute("UPDATE kanban.td_subtask SET is_done=@d WHERE id=@id",
                                new { d = op.done ?? false, id = op.id }, tx);
                            string label = conn.ExecuteScalar<string>("SELECT label FROM kanban.td_subtask WHERE id=@id", new { id = op.id }, tx);
                            activity = Log(conn, tx, TaskProject(conn, tx, op.taskId), actor, actorName,
                                (op.done == true ? "done" : "update"), "subtask", op.taskId,
                                (op.done == true ? "ทำงานย่อยเสร็จ «" : "ยกเลิกงานย่อย «") + (label ?? "") + "»");
                            break;
                        }
                        case "deleteSubtask":
                        {
                            conn.Execute("DELETE FROM kanban.td_subtask WHERE id=@id", new { id = op.id }, tx);
                            activity = Log(conn, tx, TaskProject(conn, tx, op.taskId), actor, actorName, "update", "subtask", op.taskId,
                                "ลบงานย่อย");
                            break;
                        }

                        // ---------------- COMMENTS ----------------
                        case "addComment":
                        {
                            string now = NowIso();
                            int nextOrder = conn.ExecuteScalar<int>(
                                "SELECT COALESCE(MAX(sort_order)+1,0) FROM kanban.td_comment WHERE task_id=@t", new { t = op.taskId }, tx);
                            conn.Execute("INSERT INTO kanban.td_comment (id,task_id,body,author,created_at,sort_order) VALUES (@id,@t,@b,@au,@now,@o)",
                                new { id = op.id, t = op.taskId, b = op.text ?? "", au = actorName, now = now, o = nextOrder }, tx);
                            result = new { author = actorName, timestamp = now };
                            activity = Log(conn, tx, TaskProject(conn, tx, op.taskId), actor, actorName, "comment", "comment", op.taskId,
                                "คอมเมนต์: " + Trim(op.text, 80));
                            break;
                        }

                        // ---------------- LINKS ----------------
                        case "addLink":
                        {
                            int nextOrder = conn.ExecuteScalar<int>(
                                "SELECT COALESCE(MAX(sort_order)+1,0) FROM kanban.td_link WHERE task_id=@t", new { t = op.taskId }, tx);
                            conn.Execute("INSERT INTO kanban.td_link (id,task_id,name,url,path,is_file,sort_order) VALUES (@id,@t,@n,@u,@p,@f,@o)",
                                new { id = op.id, t = op.taskId, n = op.name, u = op.url, p = op.path, f = op.isFile ?? false, o = nextOrder }, tx);
                            activity = Log(conn, tx, TaskProject(conn, tx, op.taskId), actor, actorName, "link", "link", op.taskId,
                                "แนบ" + ((op.isFile ?? false) ? "ไฟล์ " : "ลิงก์ ") + "«" + (op.name ?? "") + "»");
                            break;
                        }
                        case "deleteLink":
                        {
                            conn.Execute("DELETE FROM kanban.td_link WHERE id=@id", new { id = op.id }, tx);
                            activity = Log(conn, tx, TaskProject(conn, tx, op.taskId), actor, actorName, "update", "link", op.taskId,
                                "ลบลิงก์/ไฟล์");
                            break;
                        }

                        default:
                            throw new ArgumentException("unknown op: " + op.op);
                    }

                    tx.Commit();
                    return new { ok = true, activity = activity, result = result };
                }
            }
        }

        // =========================================================
        //  internals
        // =========================================================
        private static string FieldToColumn(string field)
        {
            switch (field)
            {
                case "text": return "title";
                case "description": return "description";
                case "priority": return "priority";
                case "due": return "due";
                case "status": return "column_id";
                case "categoryId": return "category_id";
                default: return null;
            }
        }

        private string FieldSummary(IDbConnection conn, IDbTransaction tx, string field, string value, string title)
        {
            string t = "«" + (title ?? "") + "»";
            switch (field)
            {
                case "text": return "แก้ชื่องานเป็น «" + (value ?? "") + "»";
                case "description": return "แก้ไขรายละเอียดงาน " + t;
                case "priority": return "เปลี่ยนความสำคัญงาน " + t + " เป็น " + PriorityLabel(value);
                case "due": return string.IsNullOrEmpty(value) ? ("ล้างกำหนดเสร็จงาน " + t) : ("ตั้งกำหนดเสร็จงาน " + t + " เป็น " + value);
                case "status":
                {
                    string colName = conn.ExecuteScalar<string>("SELECT name FROM kanban.tm_column WHERE id=@id", new { id = value }, tx);
                    return "ย้ายงาน " + t + " ไป «" + (colName ?? "") + "»";
                }
                case "categoryId":
                {
                    string catName = conn.ExecuteScalar<string>("SELECT name FROM kanban.tm_category WHERE id=@id", new { id = value }, tx);
                    return "เปลี่ยนหมวดงาน " + t + " เป็น «" + (catName ?? "-") + "»";
                }
                default: return "แก้ไขงาน " + t;
            }
        }

        private static string PriorityLabel(string p)
        {
            if (p == "high") return "สูง";
            if (p == "low") return "ต่ำ";
            return "กลาง";
        }

        private static string Trim(string s, int n)
        {
            s = s ?? "";
            return s.Length <= n ? s : s.Substring(0, n) + "…";
        }

        private static string TaskProject(IDbConnection conn, IDbTransaction tx, string taskId)
        {
            return conn.ExecuteScalar<string>("SELECT project_id FROM kanban.td_task WHERE id=@id", new { id = taskId }, tx);
        }

        // write one activity row, return the DTO the page consumes
        private object Log(IDbConnection conn, IDbTransaction tx, string projectId,
                           string actor, string actorName, string action, string entityType, string entityId, string summary)
        {
            string now = NowIso();
            long id = conn.ExecuteScalar<long>(
                "INSERT INTO kanban.td_activity (project_id,actor,actor_name,action,entity_type,entity_id,summary,created_at) " +
                "VALUES (@p,@a,@an,@ac,@et,@ei,@s,@t) RETURNING id",
                new { p = projectId, a = actor, an = actorName, ac = action, et = entityType, ei = entityId, s = summary, t = now }, tx);
            return new { id, projectId, actor, actorName, action, entityType, entityId, summary, createdAt = now };
        }

        // -------- default board content --------
        private void SeedDefaultProject(IDbConnection conn, IDbTransaction tx, string actor, string actorName)
        {
            string pid = NewId();
            conn.Execute("INSERT INTO kanban.tm_project (id,name,emoji,color,sort_order) VALUES (@id,@n,@e,@c,0)",
                new { id = pid, n = "งานสำนักงาน", e = "🏢", c = "#6366f1" }, tx);
            InsertDefaults(conn, tx, pid);
            Log(conn, tx, pid, actor ?? "system", actorName ?? "ระบบ", "project", "project", pid, "สร้างบอร์ดเริ่มต้น «งานสำนักงาน»");
        }

        // insert 4 columns + 5 categories; return them so the client adopts the ids
        private object InsertDefaults(IDbConnection conn, IDbTransaction tx, string projectId)
        {
            var cols = new[]
            {
                new { name = "รอดำเนินการ", color = "#64748b" },
                new { name = "กำลังทำ",     color = "#3b82f6" },
                new { name = "รอตรวจ",      color = "#f59e0b" },
                new { name = "เสร็จสิ้น",    color = "#10b981" }
            };
            var cats = new[]
            {
                new { name = "ทั่วไป",  color = "#6366f1" },
                new { name = "ด่วน",    color = "#f43f5e" },
                new { name = "ประชุม",  color = "#8b5cf6" },
                new { name = "เอกสาร",  color = "#06b6d4" },
                new { name = "ติดตาม",  color = "#10b981" }
            };

            var colOut = new List<object>();
            for (int i = 0; i < cols.Length; i++)
            {
                string cid = NewId();
                conn.Execute("INSERT INTO kanban.tm_column (id,project_id,name,color,sort_order) VALUES (@id,@pid,@n,@c,@o)",
                    new { id = cid, pid = projectId, n = cols[i].name, c = cols[i].color, o = i }, tx);
                colOut.Add(new { id = cid, name = cols[i].name, color = cols[i].color });
            }
            var catOut = new List<object>();
            for (int i = 0; i < cats.Length; i++)
            {
                string kid = NewId();
                conn.Execute("INSERT INTO kanban.tm_category (id,project_id,name,color,sort_order) VALUES (@id,@pid,@n,@c,@o)",
                    new { id = kid, pid = projectId, n = cats[i].name, c = cats[i].color, o = i }, tx);
                catOut.Add(new { id = kid, name = cats[i].name, color = cats[i].color });
            }
            return new { columns = colOut, categories = catOut };
        }

        // -------- assemble nested board JSON in the client's shape --------
        private Tuple<List<object>, List<object>> ReadBoard(IDbConnection conn, IDbTransaction tx)
        {
            var projects = conn.Query<ProjRow>("SELECT id,name,emoji,color FROM kanban.tm_project ORDER BY sort_order", null, tx).ToList();
            var columns = conn.Query<MetaRow>("SELECT id,project_id,name,color,sort_order FROM kanban.tm_column ORDER BY project_id,sort_order", null, tx).ToList();
            var categories = conn.Query<MetaRow>("SELECT id,project_id,name,color,sort_order FROM kanban.tm_category ORDER BY project_id,sort_order", null, tx).ToList();
            var tasks = conn.Query<TaskRow>("SELECT id,project_id,column_id,category_id,title,description,priority,due,sort_order,row_version,created_by,created_at,updated_at FROM kanban.td_task ORDER BY project_id,column_id,sort_order", null, tx).ToList();
            var subs = conn.Query<SubRow>("SELECT id,task_id,label,is_done,sort_order FROM kanban.td_subtask ORDER BY task_id,sort_order", null, tx).ToList();
            var coms = conn.Query<ComRow>("SELECT id,task_id,body,author,created_at,sort_order FROM kanban.td_comment ORDER BY task_id,sort_order", null, tx).ToList();
            var links = conn.Query<LinkRow>("SELECT id,task_id,name,url,path,is_file,sort_order FROM kanban.td_link ORDER BY task_id,sort_order", null, tx).ToList();

            var projOut = projects.Select(p => (object)new
            {
                id = p.id, name = p.name, emoji = p.emoji, color = p.color,
                columns = columns.Where(c => c.project_id == p.id).Select(c => new { id = c.id, name = c.name, color = c.color }).ToList(),
                categories = categories.Where(c => c.project_id == p.id).Select(c => new { id = c.id, name = c.name, color = c.color }).ToList()
            }).ToList();

            var taskOut = tasks.Select(t => (object)new
            {
                id = t.id, projectId = t.project_id, status = t.column_id, categoryId = t.category_id,
                text = t.title, description = t.description ?? "", priority = t.priority, due = t.due ?? "",
                order = t.sort_order, rowVersion = t.row_version, createdBy = t.created_by,
                createdAt = t.created_at, statusUpdatedAt = t.updated_at,
                subtasks = subs.Where(s => s.task_id == t.id).Select(s => new { id = s.id, text = s.label, done = s.is_done }).ToList(),
                comments = coms.Where(c => c.task_id == t.id).Select(c => new { id = c.id, text = c.body, author = c.author, timestamp = c.created_at }).ToList(),
                links = links.Where(l => l.task_id == t.id).Select(l => new { id = l.id, name = l.name, url = l.url, path = l.path, isFile = l.is_file }).ToList()
            }).ToList();

            return Tuple.Create(projOut, taskOut);
        }

        private object ToActivityDto(ActivityRow a)
        {
            return new
            {
                id = a.id, projectId = a.project_id, actor = a.actor, actorName = a.actor_name,
                action = a.action, entityType = a.entity_type, entityId = a.entity_id,
                summary = a.summary, createdAt = a.created_at
            };
        }

        // -------- row types (snake_case = column names, like UserModel) --------
        private class ProjRow { public string id { get; set; } public string name { get; set; } public string emoji { get; set; } public string color { get; set; } }
        private class MetaRow { public string id { get; set; } public string project_id { get; set; } public string name { get; set; } public string color { get; set; } public int sort_order { get; set; } }
        private class TaskRow
        {
            public string id { get; set; } public string project_id { get; set; } public string column_id { get; set; } public string category_id { get; set; }
            public string title { get; set; } public string description { get; set; } public string priority { get; set; } public string due { get; set; }
            public int sort_order { get; set; } public int row_version { get; set; }
            public string created_by { get; set; } public string created_at { get; set; } public string updated_at { get; set; }
        }
        private class SubRow { public string id { get; set; } public string task_id { get; set; } public string label { get; set; } public bool is_done { get; set; } public int sort_order { get; set; } }
        private class ComRow { public string id { get; set; } public string task_id { get; set; } public string body { get; set; } public string author { get; set; } public string created_at { get; set; } public int sort_order { get; set; } }
        private class LinkRow { public string id { get; set; } public string task_id { get; set; } public string name { get; set; } public string url { get; set; } public string path { get; set; } public bool is_file { get; set; } public int sort_order { get; set; } }
        private class ActivityRow
        {
            public long id { get; set; } public string project_id { get; set; } public string actor { get; set; } public string actor_name { get; set; }
            public string action { get; set; } public string entity_type { get; set; } public string entity_id { get; set; } public string summary { get; set; } public string created_at { get; set; }
        }
    }

    // ===== incoming JSON for /Kanban/Apply (camelCase = keys the page posts) =====
    public class OpRequest
    {
        public string op { get; set; }
        public string id { get; set; }
        public string projectId { get; set; }
        public string columnId { get; set; }
        public string fromColumnId { get; set; }
        public string categoryId { get; set; }
        public string taskId { get; set; }
        public string fallbackId { get; set; }
        public string name { get; set; }
        public string emoji { get; set; }
        public string color { get; set; }
        public string text { get; set; }
        public string priority { get; set; }
        public string due { get; set; }
        public string url { get; set; }
        public string path { get; set; }
        public string field { get; set; }
        public string value { get; set; }
        public int? order { get; set; }
        public int? rowVersion { get; set; }
        public int? sortOrder { get; set; }
        public bool? done { get; set; }
        public bool? isFile { get; set; }
        public bool? createDefaults { get; set; }
        public List<string> orderedIds { get; set; }
    }
}
