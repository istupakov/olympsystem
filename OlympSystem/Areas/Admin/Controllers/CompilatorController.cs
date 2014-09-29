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
using OlympSystem.Controllers;

namespace OlympSystem.Areas.Admin.Controllers
{
    [RouteArea("admin")]
    [RoutePrefix("compilators")]
    [Route("{action}/{id?}")]
    public class CompilatorController : OlympBaseController
    {
        // GET: /Admin/Compilator/
        public async Task<ActionResult> Index()
        {
            return View(await db.Compilators.ToListAsync());
        }

        // GET: /Admin/Compilator/Details/5
        public async Task<ActionResult> Details(int? id)
        {
            if (id == null)
            {
                return new HttpStatusCodeResult(HttpStatusCode.BadRequest);
            }
            Compilator compilator = await db.Compilators.FindAsync(id);
            if (compilator == null)
            {
                return HttpNotFound();
            }
            return View(compilator);
        }

        // GET: /Admin/Compilator/Create
        public ActionResult Create()
        {
            return View();
        }

        // POST: /Admin/Compilator/Create
        // To protect from overposting attacks, please enable the specific properties you want to bind to, for 
        // more details see http://go.microsoft.com/fwlink/?LinkId=317598.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<ActionResult> Create([Bind(Include="Id,Name,Language,SourceExtension,CommandLine,ConfigName,IsActive")] Compilator compilator)
        {
            if (ModelState.IsValid)
            {
                db.Compilators.Add(compilator);
                await db.SaveChangesAsync();
                return RedirectToAction("Index");
            }

            return View(compilator);
        }

        // GET: /Admin/Compilator/Edit/5
        public async Task<ActionResult> Edit(int? id)
        {
            if (id == null)
            {
                return new HttpStatusCodeResult(HttpStatusCode.BadRequest);
            }
            Compilator compilator = await db.Compilators.FindAsync(id);
            if (compilator == null)
            {
                return HttpNotFound();
            }
            return View(compilator);
        }

        // POST: /Admin/Compilator/Edit/5
        // To protect from overposting attacks, please enable the specific properties you want to bind to, for 
        // more details see http://go.microsoft.com/fwlink/?LinkId=317598.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<ActionResult> Edit([Bind(Include="Id,Name,Language,SourceExtension,CommandLine,ConfigName,IsActive")] Compilator compilator)
        {
            if (ModelState.IsValid)
            {
                db.Entry(compilator).State = EntityState.Modified;
                await db.SaveChangesAsync();
                return RedirectToAction("Index");
            }
            return View(compilator);
        }

        // GET: /Admin/Compilator/Delete/5
        public async Task<ActionResult> Delete(int? id)
        {
            if (id == null)
            {
                return new HttpStatusCodeResult(HttpStatusCode.BadRequest);
            }
            Compilator compilator = await db.Compilators.FindAsync(id);
            if (compilator == null)
            {
                return HttpNotFound();
            }
            return View(compilator);
        }

        // POST: /Admin/Compilator/Delete/5
        [HttpPost, ActionName("Delete")]
        [ValidateAntiForgeryToken]
        public async Task<ActionResult> DeleteConfirmed(int id)
        {
            Compilator compilator = await db.Compilators.FindAsync(id);
            db.Compilators.Remove(compilator);
            await db.SaveChangesAsync();
            return RedirectToAction("Index");
        }
    }
}
