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
            services.AddGrpc();

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
