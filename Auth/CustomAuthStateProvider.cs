using System.Security.Claims;
using System.Text.Json;
using Blazored.LocalStorage;
using Microsoft.AspNetCore.Components.Authorization;
using System.Net.Http.Headers;

namespace SistemaAduanero.Web.Auth
{
    public class CustomAuthStateProvider : AuthenticationStateProvider
    {
        private readonly ILocalStorageService _localStorage;
        private readonly HttpClient _http;

        public CustomAuthStateProvider(ILocalStorageService localStorage, HttpClient http)
        {
            _localStorage = localStorage;
            _http = http;
        }

        public override async Task<AuthenticationState> GetAuthenticationStateAsync()
        {
            try
            {
                Console.WriteLine("[Auth-Trace] Llamada a GetAuthenticationStateAsync() iniciada.");
                
                // Manejar si el JS todavía no está disponible, aunque con prerender:false debería estarlo.
                var token = await _localStorage.GetItemAsStringAsync("authToken");
                
                Console.WriteLine($"[Auth-Trace] Token recuperado: {(string.IsNullOrWhiteSpace(token) ? "NULL o ESPACIOS" : "OK (longitud " + token.Length + ")")}");

                // Si no hay token o está vacío, retornamos Anónimo
                if (string.IsNullOrWhiteSpace(token))
                {
                    return new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity()));
                }

                // Limpiar comillas si el token fue guardado por SetItemAsync en formato JSON
                token = token.Trim('"');

                if (string.IsNullOrWhiteSpace(token))
                {
                    return new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity()));
                }

                // Si hay token, lo configuramos en el Header HTTP
                _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("bearer", token);

                var claims = ParseClaimsFromJwt(token).ToList();

                // Validar expiración (exp)
                var expClaim = claims.FirstOrDefault(c => c.Type == "exp")?.Value;
                if (!string.IsNullOrEmpty(expClaim))
                {
                    var expTime = DateTimeOffset.FromUnixTimeSeconds(long.Parse(expClaim));
                    if (expTime <= DateTimeOffset.UtcNow)
                    {
                        Console.WriteLine("[Auth-Trace] Token expirado, limpiando sesión local.");
                        await _localStorage.RemoveItemAsync("authToken");
                        _http.DefaultRequestHeaders.Authorization = null;
                        return new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity()));
                    }
                }

                // Retornamos el estado de Autenticado
                return new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity(claims, "jwt")));
            }
            catch (InvalidOperationException ex)
            {
                // Ocurre durante Prerender
                Console.WriteLine($"[Auth] JS Interop fail (Prerender): {ex.Message}");
                return new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity()));
            }
            catch (Exception ex)
            {
                // Cualquier otro error se loguea para no perderlo
                Console.WriteLine($"[Auth] Error al obtener el estado de autenticación: {ex.Message}");
                return new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity()));
            }
        }

        public void MarkUserAsAuthenticated(string token)
        {
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("bearer", token);
            var authenticatedUser = new ClaimsPrincipal(new ClaimsIdentity(ParseClaimsFromJwt(token), "jwt"));
            var authState = Task.FromResult(new AuthenticationState(authenticatedUser));
            NotifyAuthenticationStateChanged(authState);
        }

        public void MarkUserAsLoggedOut()
        {
            var anonymousUser = new ClaimsPrincipal(new ClaimsIdentity());
            var authState = Task.FromResult(new AuthenticationState(anonymousUser));
            NotifyAuthenticationStateChanged(authState);
        }

        // Método auxiliar para leer los datos dentro del Token encriptado
        private IEnumerable<Claim> ParseClaimsFromJwt(string jwt)
        {
            var claims = new List<Claim>();
            var payload = jwt.Split('.')[1];
            var jsonBytes = ParseBase64WithoutPadding(payload);
            var keyValuePairs = JsonSerializer.Deserialize<Dictionary<string, object>>(jsonBytes);

            foreach (var kvp in keyValuePairs)
            {
                if (kvp.Value is JsonElement element && element.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in element.EnumerateArray())
                    {
                        claims.Add(new Claim(kvp.Key, item.ToString()));
                    }
                }
                else
                {
                    claims.Add(new Claim(kvp.Key, kvp.Value.ToString()));
                }
            }
            return claims;
        }

        private byte[] ParseBase64WithoutPadding(string base64)
        {
            // Arreglar codificación Base64Url a Base64 estándar
            base64 = base64.Replace('-', '+').Replace('_', '/');
            
            switch (base64.Length % 4)
            {
                case 2: base64 += "=="; break;
                case 3: base64 += "="; break;
            }
            return Convert.FromBase64String(base64);
        }

        // Método para cerrar sesión manualmente
        public async Task CerrarSesion()
        {
            await _localStorage.RemoveItemAsync("authToken");
            await _localStorage.RemoveItemAsync("termsAccepted");

            // --- CORRECCIÓN AQUÍ ---
            // Limpiamos la cabecera INMEDIATAMENTE para que la siguiente petición falle o sea anónima
            _http.DefaultRequestHeaders.Authorization = null;
            // -----------------------

            var anonymousUser = new ClaimsPrincipal(new ClaimsIdentity());
            var authState = Task.FromResult(new AuthenticationState(anonymousUser));
            NotifyAuthenticationStateChanged(authState);
        }
    }
}