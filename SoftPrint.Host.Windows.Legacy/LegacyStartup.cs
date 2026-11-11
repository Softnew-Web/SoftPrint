using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.StaticFiles;
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
            // O netcoreapp3.1 não conhece .mjs (worker do pdf.js da prévia de PDF) e responde 404.
            var contentTypes = new FileExtensionContentTypeProvider();
            contentTypes.Mappings[".mjs"] = "text/javascript";
            app.UseStaticFiles(new StaticFileOptions { ContentTypeProvider = contentTypes });
            app.UseRouting();
            app.UseMiddleware<ApiKeyMiddleware>();
            app.UseEndpoints(endpoints => endpoints.MapControllers());
        }
    }
}
