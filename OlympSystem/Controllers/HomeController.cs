using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Linq;
using System.Threading.Tasks;
using System.Web;
using System.Web.Mvc;

namespace OlympSystem.Controllers
{
   
    public class HomeController : OlympBaseController
    {
        // GET: /year
        [Route(), Route("home/{year?}")]
        public ActionResult Index(int? year)
        {
            if (year == null)
            {
                var years = db.News.Select(n => n.PublicationDate.Year).Concat(db.Contests.Select(c => c.StartTime.Year));
                year = years.Any()? years.Max(): DateTime.Now.Year;
            }
            ViewBag.Year = year;
            return View();
        }

        // GET: /news/year
        [Route("home/news/{year}")]
        public ActionResult NewsByYear(int year)
        {
            var query = db.News.Where(n => n.PublicationDate.Year == year).OrderByDescending(n => n.PublicationDate);
            return PartialView(query.ToList());
        }

        // GET: /contests/year
        [Route("home/contests/{year}")]
        public ActionResult ContestsByYear(int year)
        {
            var query = db.Contests.Where(c => !c.Hidden && c.StartTime.Year == year).OrderByDescending(c => c.StartTime);
            return PartialView(query.ToList());
        }

        //// GET: /news/
        //[Route("news/{page:int:min(0)=0}")]
        //public async Task<ActionResult> News(int? page)
        //{
        //    const int pageSize = 5;
        //    var newsCount = await db.News.CountAsync();
        //    ViewBag.PagesCount = (newsCount + pageSize - 1) / pageSize;
        //    ViewBag.Page = page;
        //    if (page != 0 && page > ViewBag.PagesCount)
        //        return HttpNotFound();


        //    var query = db.News.OrderByDescending(n => n.PublicationDate).AsQueryable();
        //    if (page.HasValue)
        //        query = query.Skip(page.Value * pageSize);

        //    return View(await query.Take(pageSize).ToListAsync());
        //}

        // GET: /about/
        [Route("about")]
        public ActionResult About()
        {
            return View();
        }

        // GET: /contact/
        [Route("contact")]
        public ActionResult Contact()
        {            
            return View();
        }
    }
}