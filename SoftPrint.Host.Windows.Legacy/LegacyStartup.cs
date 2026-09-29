using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SoftPrint.Host.Windows.Legacy.Controllers;
using SoftPrint.Infrastructure.Auth;

namespace SoftPrint.Host.Windows.Legacy
{
    internal class LegacyStartup
    {
        public LegacyStartup(IConfiguration configuration) => Configuration = configuration;
        private IConfiguration Configuration { get; }

        public void ConfigureServices(IServiceCollection services)
        {
            services.AddControllers()
                .AddApplicationPart(typeof(AppController).Assembly);
            services.AddRouting();
        }

        public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
        {
            app.UseDefaultFiles();
            app.UseStaticFiles();
            app.UseRouting();
            app.UseMiddleware<ApiKeyMiddleware>();
            app.UseEndpoints(endpoints => endpoints.MapControllers());
        }
    }
}
