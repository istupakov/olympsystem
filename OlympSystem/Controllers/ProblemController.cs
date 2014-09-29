using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Entity;
using System.Linq;
using System.Threading.Tasks;
using System.Net;
using System.Web;
using System.Web.Mvc;
using OlympSystem.Models;

namespace OlympSystem.Controllers
{
    [RoutePrefix("problems")]
    public class ProblemController : OlympBaseController
    {
        // GET: /problems/
        [Route]
        public async Task<ActionResult> Index(string contest, string name)
        {
            var problems = db.Problems.Where(p => p.Public).Include(p => p.Contest);

            if (!string.IsNullOrWhiteSpace(contest))
            {
                problems = problems.Where(p => p.Contest.Name.ToLower() == contest.ToLower());
                if(!problems.Any())
                    problems = problems.Where(p => p.Contest.Name.ToLower().Contains(contest.ToLower()));

            }
            if (!string.IsNullOrWhiteSpace(name))
            {
                problems = problems.Where(p => p.Name.ToLower() == name.ToLower());
                if (!problems.Any())
                    problems = problems.Where(p => p.Name.ToLower().Contains(name.ToLower()));
            }
            return View(await problems.ToListAsync());
        }

        // GET: /problems/5
        [Route("{id}")]
        public async Task<ActionResult> Details(int id)
        {
            Problem problem = await db.Problems.FindAsync(id);
            if (problem == null || !problem.Public)
            {
                return HttpNotFound();
            }
            return View(problem);
        }
    }
}
