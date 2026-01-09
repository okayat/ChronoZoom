using Chronozoom.Entities;
using Microsoft.IdentityModel.Claims;
using Microsoft.IdentityModel.Web;
using OuterCurve;
using SharpBrake;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Diagnostics;
using System.Linq;
using System.Security.Principal;
using System.ServiceModel.Activation;
using System.Threading;
using System.Web;
using System.Web.Compilation;
using System.Web.Mvc;
using System.Web.Optimization;
using System.Web.Routing;
using System.Web.UI;

namespace Chronozoom.UI
{
    public class Global : System.Web.HttpApplication
    {
        internal static readonly TraceListener SignalRTraceListener = new SignalRTraceListener();

        internal static TraceSource Trace { get; set; }

        internal class WebFormRouteHandler<T> : IRouteHandler where T : IHttpHandler, new()
        {
            public string VirtualPath { get; set; }

            public WebFormRouteHandler(string virtualPath)
            {
                this.VirtualPath = virtualPath;
            }

            public IHttpHandler GetHttpHandler(RequestContext requestContext)
            {
                return (VirtualPath != null)
                    ? (IHttpHandler)BuildManager.CreateInstanceFromVirtualPath(VirtualPath, typeof(T))
                    : new T();
            }
        }

        internal class UserAgentConstraint : IRouteConstraint
        {
            [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1062:Validate arguments of public methods", MessageId = "0")]
            public bool Match(HttpContextBase httpContext, Route route, string parameterName, RouteValueDictionary values, RouteDirection routeDirection)
            {
                return httpContext.Request.Browser.Crawler;
            }
        }

        internal static void RegisterRoutes(RouteCollection routes)
        {
            var routeHandlerDetails = new WebFormRouteHandler<DefaultHttpHandler>(null);
            var crawlerRouteHandler = new WebFormRouteHandler<Page>("/pages/crawler.aspx");

            routes.MapRoute(
                "Account", // Route name
                "account/{action}", // URL with parameters
                new { controller = "Account" } // Parameter defaults
                );

            routes.Add(new ServiceRoute("api", new WebServiceHostFactory(), typeof(ChronozoomSVC)));

            routes.Add(new Route("sitemap.xml", new WebFormRouteHandler<Page>("/pages/sitemap.aspx")));

            RouteValueDictionary crawlerConstraint = new RouteValueDictionary()
            {
                { "crawler", new UserAgentConstraint() }
            };

            AddFriendlyUrlRoutes(routes, crawlerRouteHandler, crawlerConstraint);
            AddFriendlyUrlRoutes(routes, routeHandlerDetails, null);
        }

        private static void AddFriendlyUrlRoutes(RouteCollection routes, IRouteHandler routeHandlerDetails, RouteValueDictionary constraint)
        {
            routes.Add(new Route("{supercollection}", null, constraint, routeHandlerDetails));
            routes.Add(new Route("{supercollection}/{collection}", null, constraint, routeHandlerDetails));
            routes.Add(new Route("{supercollection}/{collection}/{reference}", null, constraint, routeHandlerDetails));
            routes.Add(new Route("{supercollection}/{collection}/{timelineTitle}/{reference}", null, constraint, routeHandlerDetails));
            routes.Add(new Route("{supercollection}/{collection}/{timelineTitle}/{exhibitTitle}/{reference}", null, constraint, routeHandlerDetails));
            routes.Add(new Route("{supercollection}/{collection}/{timelineTitle}/{exhibitTitle}/{contentItemTitle}/{reference}", null, constraint, routeHandlerDetails));
        }

        public void Application_Start(object sender, EventArgs e)
        {
            Trace = new TraceSource("Global", SourceLevels.All);
            Trace.Listeners.Add(SignalRTraceListener);
            Storage.Trace.Listeners.Add(SignalRTraceListener);

            RouteTable.Routes.MapHubs();
            RegisterRoutes(RouteTable.Routes);

            BundleTable.EnableOptimizations = true; // enables bundling for debug mode
            BundleConfig.RegisterBundles(BundleTable.Bundles);

            Trace.TraceInformation("Checking Db Schema");
            using (Entities.ManualMigrationCheck check = new Entities.ManualMigrationCheck())
            {
                if (check.NewInstall)
                {
                    Trace.TraceInformation("New Install - Populating Initial Db Content");
                    using (Utils.PopulateDbFromJSON populator = new Utils.PopulateDbFromJSON())
                    {
                        populator.ImportCollection("ChronoZoom", "Cosmos", "cz.cosmos.json", true, true, true);
                        populator.ImportCollection("ChronoZoom", "AIDS Timeline", "cz.aidstimeline.json", false, true, true);
                    }
                }
            }

            Trace.TraceInformation("Application Starting");
        }

        protected void Application_PostAuthenticateRequest(object sender, EventArgs e)
        {
            if (!string.Equals(ConfigurationManager.AppSettings["DevAuth"], "true", StringComparison.OrdinalIgnoreCase))
                return;

            var ctx = HttpContext.Current;
            if (ctx?.User?.Identity != null && ctx.User.Identity.IsAuthenticated)
                return;

            const string devNameId = "dev@local.test";
            const string devIdp = "DEV";

            var claims = new List<Microsoft.IdentityModel.Claims.Claim>
    {
        new Microsoft.IdentityModel.Claims.Claim(Microsoft.IdentityModel.Claims.ClaimTypes.NameIdentifier, devNameId),
        new Microsoft.IdentityModel.Claims.Claim("http://schemas.microsoft.com/accesscontrolservice/2010/07/claims/identityprovider", devIdp),
        new Microsoft.IdentityModel.Claims.Claim(Microsoft.IdentityModel.Claims.ClaimTypes.Name, "Dev")
    };

            var wifIdentity = new Microsoft.IdentityModel.Claims.ClaimsIdentity(claims, "DEV");

            System.Security.Principal.IPrincipal wifPrincipal =
                new Microsoft.IdentityModel.Claims.ClaimsPrincipal(
                    new Microsoft.IdentityModel.Claims.IClaimsIdentity[] { wifIdentity }
                );

            ctx.User = wifPrincipal;
            System.Threading.Thread.CurrentPrincipal = wifPrincipal;

            var threadUser = System.Threading.Thread.CurrentPrincipal;
            var identity = ctx?.User?.Identity;

            Debug.WriteLine("\n==================================================");
            Debug.WriteLine("               AUTHENTIFIZIERUNGS CHECK            ");
            Debug.WriteLine("==================================================");

            Debug.WriteLine("HTTP Context vorhanden: " + (ctx != null));
            Debug.WriteLine("Thread Principal gesetzt: " + (threadUser != null));
            Debug.WriteLine("User Identity vorhanden: " + (identity != null));

            Debug.WriteLine("IsAuthenticated (HTTP): " + ctx?.User?.Identity?.IsAuthenticated);
            Debug.WriteLine("AuthType: " + ctx?.User?.Identity?.AuthenticationType);
            Debug.WriteLine("Identity Typ: " + identity?.GetType().FullName);

            if (identity is Microsoft.IdentityModel.Claims.ClaimsIdentity wifId)
            {
                Debug.WriteLine("\n--- WIF ClaimsIdentity erkannt ---");
                Debug.WriteLine("WIF IsAuthenticated: " + wifId.IsAuthenticated);
                Debug.WriteLine("Anzahl Claims: " + wifId.Claims.Count());

                foreach (var claim in wifId.Claims)
                {
                    Debug.WriteLine("Claim: " + claim.ClaimType + " = " + claim.Value);
                }
            }
            else
            {
                Debug.WriteLine("\n⚠ Achtung: Keine WIF ClaimsIdentity aktiv!");
            }

            Debug.WriteLine("\n==================================================\n");
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Performance", "CA1811:AvoidUncalledPrivateCode")]
        void SessionAuthenticationModule_SessionSecurityTokenReceived(object sender, SessionSecurityTokenReceivedEventArgs e)
        {
            int minutes = 60;
            DateTime now = DateTime.UtcNow;
            DateTime validTo = e.SessionToken.ValidTo;

            //This can reduce token updates count
            if (now < validTo)
            {
                SessionAuthenticationModule sam = sender as SessionAuthenticationModule;
                e.SessionToken = sam.CreateSessionSecurityToken(e.SessionToken.ClaimsPrincipal, e.SessionToken.Context, now, now.AddMinutes(minutes), e.SessionToken.IsPersistent);
                e.ReissueCookie = true;
            }
        }

        public void Application_End(object sender, EventArgs e)
        {
        }

        public void Application_Error(object sender, EventArgs e)
        {
            Exception lastError = Server.GetLastError();
            if (ConfigurationManager.AppSettings["Airbrake.TrackServer"].ToLower() == "true") lastError.SendToAirbrake();
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Performance", "CA1811:AvoidUncalledPrivateCode")]
        public void Application_BeginRequest(object sender, EventArgs e)
        {
            var app = (HttpApplication)sender;

            if (app.Context.Request.Url.LocalPath == "/")
            {
                if (Request.Browser.Crawler)
                {
                    app.Context.RewritePath(string.Concat(app.Context.Request.Url.LocalPath, "/pages/crawler.aspx"));
                }
                else
                {
                    if (BrowserIsSupported())
                    {
                        app.Context.RewritePath(string.Concat(app.Context.Request.Url.LocalPath, "default.ashx"));
                    }
                    else
                    {
                        app.Context.RewritePath(string.Concat(app.Context.Request.Url.LocalPath, "fallback.html"));
                    }
                }
            }
        }

        // Supported versions - Moved from JavaScript and added Opera
        private static readonly Dictionary<string, int> _supportedMatrix = new Dictionary<string, int>()
        {
            { "IE", 9 },
            { "Firefox", 7 },
            { "Chrome", 14 },
            { "Safari", 5 },
            { "Opera", 10 },
        };


        private bool BrowserIsSupported()
        {
            System.Web.HttpBrowserCapabilities browser = Request.Browser;

            if (_supportedMatrix.ContainsKey(browser.Browser))
            {
                return Double.Parse(browser.Version, System.Globalization.CultureInfo.InvariantCulture) >= _supportedMatrix[browser.Browser];
            }

            return true;
        }
    }
}