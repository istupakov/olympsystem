using System;
using System.Globalization;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using System.Web;
using System.Web.Mvc;
using Microsoft.AspNet.Identity;
using Microsoft.AspNet.Identity.Owin;
using Microsoft.Owin.Security;
using OlympSystem.Models;

namespace OlympSystem.Controllers
{
    public abstract class OlympBaseController : Controller
    {
        protected OlympDbContext db;
        private ApplicationUserManager manager;

        protected async Task<ApplicationUser> GetCurrentUserAsync()
        {
            if(manager == null)
                manager = HttpContext.GetOwinContext().GetUserManager<ApplicationUserManager>();
            return await manager.FindByIdAsync(User.Identity.GetUserId());
        }

        protected OlympBaseController()
        {
            db = new OlympDbContext();
           
        }

        //protected override void Dispose(bool disposing)
        //{
        //    if (disposing)
        //    {
        //        db.Dispose();
        //        manager.Dispose();
        //    }
        //    base.Dispose(disposing);
        //}
	}
}