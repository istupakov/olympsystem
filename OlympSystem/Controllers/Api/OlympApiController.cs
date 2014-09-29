using System.Data.Entity;
using System.Data.Entity.Infrastructure;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using System.Web.Http;
using System.Web.Http.Description;
using OlympSystem.Models;
using System.Collections.Generic;

namespace OlympSystem.Api.Controllers
{
    public abstract class OlympApiController<T> : ApiController
        where T: class
    {
        protected OlympDbContext db = new OlympDbContext();
        protected DbSet<T> set;

        protected abstract bool ItemExists(int id);

        [NonAction]
        public async Task<IHttpActionResult> FindAsync(int id)
        {
            var res = await set.FindAsync(id);
            if (res == null)
            {
                return NotFound();
            }

            return Ok(res);
        }

        [NonAction]
        public async Task<IHttpActionResult> SaveAsync(int id, T item)
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(ModelState);
            }

            db.Entry(item).State = EntityState.Modified;

            try
            {
                await db.SaveChangesAsync();
            }
            catch (DbUpdateConcurrencyException)
            {
                if (!ItemExists(id))
                    return NotFound();

                throw;
            }

            return StatusCode(HttpStatusCode.NoContent);
        }

        [NonAction]
        public async Task<IHttpActionResult> AddAsync(int id, T item)
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(ModelState);
            }

            set.Add(item);
            await db.SaveChangesAsync();

            return CreatedAtRoute("DefaultApi", new { id }, item);
        }

        [NonAction]
        public async Task<IHttpActionResult> RemoveAsync(int id)
        {
            var item = await set.FindAsync(id);
            if (item == null)
            {
                return NotFound();
            }

            set.Remove(item);
            await db.SaveChangesAsync();

            return Ok(item);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                db.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    public class NewsController : OlympApiController<News>
    {
        public NewsController()
        {
            set = db.News;
        }

        protected override bool ItemExists(int id)
        {
            return set.Count(e => e.Id == id) > 0;
        }

        public IQueryable<News> Get()
        {
            return set;
        }

        [ResponseType(typeof(News))]
        public async Task<IHttpActionResult> Get(int id)
        {
            return await FindAsync(id);
        }

        [ResponseType(typeof(void))]
        public async Task<IHttpActionResult> Put(int id, News item)
        {
            if (id != item.Id)
                return BadRequest();

            return await SaveAsync(id, item);
        }

        [ResponseType(typeof(News))]
        public async Task<IHttpActionResult> Post(News item)
        {
            return await AddAsync(item.Id, item);
        }

        [ResponseType(typeof(News))]
        public async Task<IHttpActionResult> Delete(int id)
        {
            return await RemoveAsync(id);
        }
    }

    public class CompilatorsController : OlympApiController<Compilator>
    {
        public CompilatorsController()
        {
            set = db.Compilators;
        }

        protected override bool ItemExists(int id)
        {
            return set.Count(e => e.Id == id) > 0;
        }

        public IEnumerable<Compilator> Get()
        {
            return set;
        }

        [ResponseType(typeof(Compilator))]
        public async Task<IHttpActionResult> Get(int id)
        {
            return await FindAsync(id);
        }

        [ResponseType(typeof(void))]
        public async Task<IHttpActionResult> Put(int id, Compilator item)
        {
            if (id != item.Id)
                return BadRequest();

            return await SaveAsync(id, item);
        }

        [ResponseType(typeof(Compilator))]
        public async Task<IHttpActionResult> Post(Compilator item)
        {
            return await AddAsync(item.Id, item);
        }

        [ResponseType(typeof(Compilator))]
        public async Task<IHttpActionResult> Delete(int id)
        {
            return await RemoveAsync(id);
        }
    }

    public class UsersController : OlympApiController<User>
    {
        public UsersController()
        {
            set = db.OlympUsers;
        }

        protected override bool ItemExists(int id)
        {
            return set.Count(e => e.Id == id) > 0;
        }

        public IQueryable<User> Get()
        {
            return set;
        }
    }
}
