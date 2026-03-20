using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace SistemaAduanero.Web.Auth
{
    public class ApiAuthorizationMessageHandler : DelegatingHandler
    {
        private readonly NavigationManager _navigationManager;
        private readonly IServiceProvider _serviceProvider;

        public ApiAuthorizationMessageHandler(NavigationManager navigationManager, IServiceProvider serviceProvider)
        {
            _navigationManager = navigationManager;
            _serviceProvider = serviceProvider;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken);
            
            if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
            {
                var authStateProvider = _serviceProvider.GetRequiredService<AuthenticationStateProvider>() as CustomAuthStateProvider;
                if (authStateProvider != null)
                {
                    await authStateProvider.CerrarSesion();
                }
                
                _navigationManager.NavigateTo("/login", forceLoad: true);
            }

            return response;
        }
    }
}
