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