using System;
using System.Collections.Generic;
using System.Linq;
using System.Web;
using System.Web.Mvc;

namespace OlympSystem.Areas.Admin.Controllers
{
    [RouteArea("admin")]    
    public class HomeController : Controller
    {
        // GET: /Admin/
        [Route]
        public ActionResult Index()
        {
            return View();
        }
	}
}