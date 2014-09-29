using System;
using System.Collections.Generic;
using System.Linq;
using System.Web;
using System.Web.Mvc;
using System.Data;
using System.Data.Entity;
using System.Threading.Tasks;
using OlympSystem.Models;

namespace OlympSystem.Controllers
{
    [RoutePrefix("submits")]
    public class SubmitController : OlympBaseController
    {
        // GET: /submits/
        [Route, Route("~/problems/{problemId}/submits")]
        public async Task<ActionResult> Index(int? problemId)
        {
            var submits = db.Solutions.OrderByDescending(s => s.CommitTime).Where(s => !s.Hidden && s.Problem.Public && !s.User.IsHidden && !(s.User is Competitor));
            if (problemId.HasValue)
                submits = submits.Where(s => s.ProblemId == problemId);
            return View(await submits.Include(s => s.User).Include(s => s.Problem.Contest).Take(200).ToListAsync());
        }

        [Route("~/problems/{problemId}/submit")]
        public async Task<ActionResult> Send(int problemId)
        {
            ViewBag.CompilatorList = await db.Compilators.Where(c => c.IsActive).ToListAsync();
            return View(new SubmitSolutionViewModel { ProblemId = problemId });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [Route("~/problems/{problemId}/submit")]
        public async Task<ActionResult> Send(SubmitSolutionViewModel compilator)
        {
            if (ModelState.IsValid)
            {            
                return RedirectToAction("Index");
            }
            ViewBag.CompilatorList = await db.Compilators.Where(c => c.IsActive).ToListAsync();
            return View(compilator);
        }


	}
}