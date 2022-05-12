using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using Serilog.Ui.Core;
using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Serilog.Ui.Web
{
    public class SerilogUiMiddleware
    {
        private const string EmbeddedFileNamespace = "Serilog.Ui.Web.wwwroot.dist";
        private readonly UiOptions _options;
        private readonly StaticFileMiddleware _staticFileMiddleware;
        private readonly JsonSerializerSettings _jsonSerializerOptions;
        private readonly ILogger<SerilogUiMiddleware> _logger;

        public SerilogUiMiddleware(
            RequestDelegate next,
            IWebHostEnvironment hostingEnv,
            ILoggerFactory loggerFactory,
            UiOptions options,
            ILogger<SerilogUiMiddleware> logger
            )
        {
            _options = options;
            _logger = logger;
            _staticFileMiddleware = CreateStaticFileMiddleware(next, hostingEnv, loggerFactory);
            _jsonSerializerOptions = new JsonSerializerSettings
            {
                NullValueHandling = NullValueHandling.Ignore,
                ContractResolver = new CamelCasePropertyNamesContractResolver(),
                Formatting = Formatting.None
            };
        }

        public async Task Invoke(HttpContext httpContext)
        {
            var httpMethod = httpContext.Request.Method;
            var path = httpContext.Request.Path.Value;

            // If the RoutePrefix is requested (with or without trailing slash), redirect to index URL
            if (httpMethod == "GET" && Regex.IsMatch(path, $"^/{Regex.Escape(_options.RoutePrefix)}/api/logs/?$", RegexOptions.IgnoreCase))
            {
                try
                {
                    httpContext.Response.ContentType = "application/json;charset=utf-8";
                    if (!CanAccess(httpContext))
                    {
                        httpContext.Response.StatusCode = (int)HttpStatusCode.Forbidden;
                        return;
                    }

                    var result = await FetchLogsAsync(httpContext);
                    httpContext.Response.StatusCode = (int)HttpStatusCode.OK;
                    await httpContext.Response.WriteAsync(result);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, ex.Message);
                    httpContext.Response.StatusCode = (int)HttpStatusCode.InternalServerError;

                    var errorMessage = httpContext.Request.IsLocal()
                        ? JsonConvert.SerializeObject(new { errorMessage = ex.Message })
                        : JsonConvert.SerializeObject(new { errorMessage = "Internal server error" });

                    await httpContext.Response.WriteAsync(JsonConvert.SerializeObject(new { errorMessage }));
                }

                return;
            }

            if (httpMethod == "GET" && Regex.IsMatch(path, $"^/?{Regex.Escape(_options.RoutePrefix)}/?$", RegexOptions.IgnoreCase))
            {
                var indexUrl = httpContext.Request.GetEncodedUrl().TrimEnd('/') + "/index.html";
                RespondWithRedirect(httpContext.Response, indexUrl);
                return;
            }

            if (httpMethod == "GET" && Regex.IsMatch(path, $"^/{Regex.Escape(_options.RoutePrefix)}/?index.html$", RegexOptions.IgnoreCase))
            {
                await RespondWithIndexHtml(httpContext.Response);
                return;
            }

            await _staticFileMiddleware.Invoke(httpContext);
        }

        private StaticFileMiddleware CreateStaticFileMiddleware(
            RequestDelegate next,
            IWebHostEnvironment hostingEnv,
            ILoggerFactory loggerFactory)
        {
            var staticFileOptions = new StaticFileOptions
            {
                RequestPath = $"/{_options.RoutePrefix}",
                FileProvider = new EmbeddedFileProvider(typeof(SerilogUiMiddleware).GetTypeInfo().Assembly, EmbeddedFileNamespace),
            };

            return new StaticFileMiddleware(next, hostingEnv, Options.Create(staticFileOptions), loggerFactory);
        }

        private void RespondWithRedirect(HttpResponse response, string location)
        {
            response.StatusCode = 301;
            response.Headers["Location"] = location;
        }

        private async Task RespondWithIndexHtml(HttpResponse response)
        {
            response.StatusCode = 200;
            response.ContentType = "text/html;charset=utf-8";

            await using var stream = IndexStream();
            var htmlBuilder = new StringBuilder(await new StreamReader(stream).ReadToEndAsync());
            var encodeAuthOpts = Uri.EscapeDataString(JsonConvert.SerializeObject(new { _options.RoutePrefix, _options.AuthType }, _jsonSerializerOptions));
            htmlBuilder.Replace("%(Configs)", encodeAuthOpts);

            await response.WriteAsync(htmlBuilder.ToString(), Encoding.UTF8);
        }

        private Func<Stream> IndexStream { get; } = () => typeof(AuthorizationOptions).GetTypeInfo().Assembly
            .GetManifestResourceStream("Serilog.Ui.Web.wwwroot.dist.index.html");

        private async Task<string> FetchLogsAsync(HttpContext httpContext)
        {
            httpContext.Request.Query.TryGetValue("page", out var pageStr);
            httpContext.Request.Query.TryGetValue("count", out var countStr);
            httpContext.Request.Query.TryGetValue("level", out var levelStr);
            httpContext.Request.Query.TryGetValue("user", out var user);
            httpContext.Request.Query.TryGetValue("search", out var searchStr);
            httpContext.Request.Query.TryGetValue("startDate", out var startDateStar);
            httpContext.Request.Query.TryGetValue("endDate", out var endDateStar);

            int.TryParse(pageStr, out var currentPage);
            int.TryParse(countStr, out var count);

            DateTime.TryParse(startDateStar, out var startDate);
            DateTime.TryParse(endDateStar, out var endDate);

            if (endDate != default)
                endDate = new DateTime(endDate.Year, endDate.Month, endDate.Day, 23, 59, 59);

            currentPage = currentPage == default ? 1 : currentPage;
            count = count == default ? 10 : count;

            var provider = httpContext.RequestServices.GetService<IDataProvider>();
            var (logs, total) = await provider.FetchDataAsync(currentPage, count, levelStr, searchStr,
                user, startDate == default ? (DateTime?)null : startDate, endDate == default ? (DateTime?)null : endDate);
            //var result = JsonSerializer.Serialize(logs, _jsonSerializerOptions);
            var result = JsonConvert.SerializeObject(new { logs, total, count, currentPage }, _jsonSerializerOptions);
            return result;
        }

        private static bool CanAccess(HttpContext httpContext)
        {
            if (httpContext.Request.IsLocal())
                return true;

            var authOptions = httpContext.RequestServices.GetService<AuthorizationOptions>();
            
            // if not authorization is required return true
            if (!authOptions.Enabled)
                return true;

            if (!httpContext.User.Identity.IsAuthenticated)
                return false;

            var userName = httpContext.User.Identity.Name?.ToLower();
            if (authOptions.Usernames != null &&
                authOptions.Usernames.Any(u => u.ToLower() == userName))
                return true;

            if (authOptions.Roles != null &&
                authOptions.Roles.Any(role => httpContext.User.IsInRole(role)))
                return true;

            return false;
        }
    }
}