# IA.md — Registro de uso de IA

Herramienta utilizada: **Claude**

A continuación se listan los prompts utilizados durante el desarrollo del Taller 1, junto con una breve descripción de para qué sirvió cada uno y qué se generó a partir de él.

---

**Prompt:**
> "a que se refiere con ocultar timestamp del ULID?"

**Uso:** Se pidió primero una explicación del problema (por qué el ULID expuesto filtra la hora de creación) y el paso a paso de la solución, sin aplicar cambios todavía.

**Resultado:** Se modificó `Application/Services/LinkService.cs` para generar el `shortUrl` a partir de un hash SHA-256 del ULID codificado en Base62.

---

**Prompt:**
> "donde tengo que agregar cache-control, etag y last modified?"

**Resultado:** Se explicó y se agregó el campo `CreatedAt` a la entidad `Link`, y en `Endpoints/UrlRedirectEndpoint.cs` se implementó el cálculo de `ETag` y `Last-Modified`.

---

**Prompt:**
> "como configuro globalmente Strict-Transport-Security, X-Content-Type-Options, X-Frame-Options, Referrer-Policy y Permissions-Policy"

**Resultado:** explicó y se creó `Middleware/SecurityHeadersMiddleware.cs` con `Strict-Transport-Security`, `X-Content-Type-Options`, `X-Frame-Options`, `Referrer-Policy` y `Permissions-Policy`, registrado en `Program.cs`.

---

**Prompt:** 
> "explica como se crea un midelware y que es xrespondetime"

**Resultado:** Se creó `Middleware/PerformanceMiddleware.cs`, que agrega el header `X-Response-Time` y registra un log de advertencia dedicado para requests que superan los 500ms.

---

**Prompt:** 
>"como se protege los inicios de sesion a nivel de framework?"

**Resultado:** se dió una breve explicación y luego se eliminó el throttling manual con `ConcurrentDictionary` en `UserService.Login` y se reemplazó por el rate limiter nativo de ASP.NET Core, con una policy `"login"` particionada por IP, aplicada en `Pages/Login.cshtml.cs` vía `[EnableRateLimiting("login")]`, devolviendo `429` con `Retry-After`.

---

**Prompt:**
> "como habilito compresión brotli y gzip en asp.net core y donde la ubico en el pipeline?"

**Uso:** Se pidió una explicación de cómo activar la compresión de respuestas y en qué orden del middleware debía ir respecto a lo ya configurado.

**Resultado:** Se agregó `AddResponseCompression()` con `BrotliCompressionProvider` y `GzipCompressionProvider` en `Program.cs`, con `EnableForHttps` desactivado (riesgo BREACH en contenido dinámico con secretos), y `app.UseResponseCompression()` ubicado en el pipeline.

---

**Prompt:**
> "como configuro una política de cors restrictiva en vez de allowanyorigin?"

**Uso:** Se pidió una explicación de cómo definir una política CORS explícita (orígenes/métodos/headers) y dónde aplicarla.

**Resultado:** Se agregó `AddCors()` con la política `"ApiCors"` en `Program.cs`, `app.UseCors()` en el pipeline, y `.RequireCors("ApiCors")` aplicado al endpoint `GET /{shortUrl}` en `UrlRedirectEndpoint.cs`.

---

**Prompt:**
> "como devuelvo errores en formato application/problem+json en vez de texto plano?"

**Uso:** Se pidió una explicación de RFC 9457/problem+json y cómo reemplazar las respuestas de error existentes.

**Resultado:** Se agregó validación de formato del `shortUrl` (400 vía `Results.Problem()`) y se modificó el `catch (KeyNotFoundException)` para devolver 404 en el mismo formato, en `UrlRedirectEndpoint.cs`. Se detectó y corrigió un bug durante las pruebas: la validación inicial asumía longitud fija de 12 caracteres, rompiendo los shortUrls sembrados (`aspnet`, `github`, `efcore`); se corrigió para validar solo charset base62 y el `MaxLength` real (32).

---

**Prompt:**
> "que flags de seguridad le tengo que poner a las cookies de la app?"

**Uso:** Se pidió una explicación de HttpOnly, SameSite, Secure y Path, y una auditoría de dónde se escribían cookies en el proyecto.

**Resultado:** Se identificaron dos cookies (autenticación y antiforgery) y se configuraron explícitamente en `Program.cs` con `HttpOnly=true`, `SameSite=Strict`, `Path="/"` y `SecurePolicy` condicionado al entorno.

---

**Prompt:**
> "cuando corresponde usar 301 vs 302 vs 307 en una redirección?"

**Uso:** Se pidió una explicación de la semántica de cada código y cómo aplicarla según el estado del link (clicks, antigüedad).

**Resultado:** Se modificó `UrlRedirectEndpoint.cs` para devolver `301` (links con >100 clicks, con `Cache-Control: public, max-age=300, must-revalidate`), `307` (links nuevos, 0 clicks y <24h) y `302` como fallback, preservando la validación y el manejo 404 del ítem #8.