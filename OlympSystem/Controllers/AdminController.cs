using OlympSystem.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Web;
using System.Web.Mvc;

namespace OlympSystem.Controllers
{
    public class AdminController : Controller
    {
        public ActionResult Index(string name)
        {
            return View();
        }

        [Route("admin/partial/{name}")]
        public ActionResult Partial(string name)
        {
            return PartialView(name);
        }

        [Route("admin/resetdb")]
        public ActionResult ResetDB()
        {
            var db = new OlympDbContext();
            HttpContext.GetOwinContext().Authentication.SignOut();
            db.DeleteDB();
            return RedirectToAction("Index");
        }
    }
}