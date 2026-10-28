using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace PnP.Scanning.Process
{
    internal class Startup<T> where T : class
    {
        public void ConfigureServices(IServiceCollection services)
        {
            // The explicit catalog is frozen in StartRequest properties (base64 adds 4/3 overhead).
            // Keep a finite native receive budget large enough for the supported catalog size.
            services.AddGrpc(options => options.MaxReceiveMessageSize =
                PnP.Scanning.Core.Discovery.M1.PlatformAspxCatalog.MaximumTransportMessageBytes);

            // Configure the shutdown to 15s - not yet working
            //services.Configure<HostOptions>(
            //    opts => opts.ShutdownTimeout = TimeSpan.FromSeconds(15));
        }

        public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
        {
            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }

            app.UseRouting();

            app.UseEndpoints(endpoints =>
            {
                endpoints.MapGrpcService<T>();
            });
        }
    }
}
