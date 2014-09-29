using Microsoft.Owin;
using Owin;

[assembly: OwinStartupAttribute(typeof(OlympSystem.Startup))]
namespace OlympSystem
{
    public partial class Startup
    {
        public void Configuration(IAppBuilder app)
        {
            ConfigureAuth(app);
        }
    }
}
