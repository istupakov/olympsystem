using OlympSystem.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Web;
using System.Web.Mvc;

namespace OlympSystem.Controllers
{
    [RoutePrefix("admin")]
    public class AdminController : Controller
    {
        [Route()]
        public ActionResult Index(string name)
        {
            return View();
        }

        [Route("partial/{name}")]
        public ActionResult Partial(string name)
        {
            return PartialView(name);
        }

        [Route("resetdb")]
        public ActionResult ResetDB()
        {
            var db = new OlympDbContext();
            HttpContext.GetOwinContext().Authentication.SignOut();
            db.DeleteDB();
            return RedirectToAction("Index");
        }
    }
}