using System;
using System.Text;
using System.Web.Mvc;
using TabletCheckIn.Repositories;

namespace TabletCheckIn.Controllers
{
    // Nexus Board — 1 controller + 1 view, multi-user.
    //   GET  /Kanban            -> the board page
    //   GET  /Kanban/GetState   -> full board + recent activity (auto-seeds if empty)
    //   GET  /Kanban/GetChanges -> activity newer than ?since=<id> (10s polling)
    //   POST /Kanban/Apply      -> one granular operation { op, ... }
    // Concurrency is handled per-task via row_version; a clash returns HTTP 409.
    public class KanbanController : Controller
    {
        private readonly KanbanRepository _repo = new KanbanRepository();

        private string Actor { get { return (Session["Username"] ?? "").ToString(); } }
        private string ActorName
        {
            get
            {
                var n = Session["FullName"] ?? Session["Username"];
                return (n ?? "").ToString();
            }
        }

        [HttpGet]
        public ActionResult Index()
        {
            if (Session["Username"] == null)
                return RedirectToAction("Index", "Auth");

            ViewBag.Title = "Kanban Board";
            return View();
        }

        [HttpGet]
        public ActionResult GetState()
        {
            if (Session["Username"] == null)
            {
                Response.StatusCode = 401;
                return Json(new { error = "unauthorized" }, JsonRequestBehavior.AllowGet);
            }
            try
            {
                var data = _repo.GetBoard(Actor, ActorName);
                return BigJson(data);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Kanban.GetState] " + ex);
                Response.StatusCode = 500;
                return Json(new { error = ex.Message }, JsonRequestBehavior.AllowGet);
            }
        }

        [HttpGet]
        public ActionResult GetChanges(long since = 0)
        {
            if (Session["Username"] == null)
            {
                Response.StatusCode = 401;
                return Json(new { error = "unauthorized" }, JsonRequestBehavior.AllowGet);
            }
            try
            {
                var data = _repo.GetChanges(since);
                return BigJson(data);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Kanban.GetChanges] " + ex);
                Response.StatusCode = 500;
                return Json(new { error = ex.Message }, JsonRequestBehavior.AllowGet);
            }
        }

        [HttpPost]
        public JsonResult Apply(OpRequest req)
        {
            if (Session["Username"] == null)
            {
                Response.StatusCode = 401;
                return Json(new { ok = false, error = "unauthorized" });
            }
            try
            {
                var data = _repo.Apply(Actor, ActorName, req);
                return Json(data);
            }
            catch (ConcurrencyException)
            {
                // someone edited the same task first — tell the page to refresh
                Response.StatusCode = 409;
                return Json(new { ok = false, error = "conflict" });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Kanban.Apply] " + ex);
                Response.StatusCode = 500;
                return Json(new { ok = false, error = ex.GetBaseException().Message });
            }
        }

        // JsonResult that won't choke on a large board
        private ActionResult BigJson(object data)
        {
            return new JsonResult
            {
                Data = data,
                JsonRequestBehavior = JsonRequestBehavior.AllowGet,
                MaxJsonLength = int.MaxValue,
                ContentEncoding = Encoding.UTF8
            };
        }
    }
}