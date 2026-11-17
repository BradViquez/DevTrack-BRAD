using Microsoft.AspNetCore.DataProtection;
using System.Net;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
builder.Services.AddDistributedMemoryCache();
builder.Services.AddSession(options => options.Cookie.SameSite = SameSiteMode.Strict);
builder.Services.AddAntiforgery();
var app = builder.Build();
app.UseSession();
var gate = new object();
var path = builder.Configuration["DevTrack:Archivo"] ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DevTrackWeb", "datos.json");
var data = File.Exists(path) ? JsonSerializer.Deserialize<Datos>(File.ReadAllText(path))! : new Datos();
var deshacer = new Stack<Datos>();
string[] roles = ["Administrador", "Líder", "Desarrollador", "QA"];
string[] prioridades = ["Alta", "Media", "Baja"];
string[] estados = ["Pendiente", "En desarrollo", "En revisión QA", "Aprobada", "Rechazada"];
bool Proyectos(string? rol) => rol is "Administrador" or "Líder";
bool Calidad(string? rol) => rol is "Administrador" or "QA";
bool Editar(string? rol) => Proyectos(rol) || rol == "Desarrollador";
string E(string? s) => WebUtility.HtmlEncode(s ?? "");
string Campo(string nombre, string etiqueta, string valor = "", string tipo = "text") =>
    $"""<label>{E(etiqueta)}<input required name="{nombre}" value="{E(valor)}" type="{tipo}" maxlength="140"></label>""";
string Lista(string nombre, string etiqueta, IEnumerable<string> opciones, string? actual = null) =>
    $"""<label>{E(etiqueta)}<select required name="{nombre}">{string.Concat(opciones.Select(x => $"<option{(x == actual ? " selected" : "")}>{E(x)}</option>"))}</select></label>""";
string Form(string token, string accion, string cuerpo, string boton) =>
    $"""<form method="post"><input type="hidden" name="__RequestVerificationToken" value="{E(token)}"><input type="hidden" name="accion" value="{accion}">{cuerpo}<button>{boton}</button></form>""";
string Id(string id) => $"""<input type="hidden" name="id" value="{E(id)}">""";

Queue<Tarea> ColaQa(Datos origen) => new(origen.Tareas
    .Where(t => t.Estado == estados[2])
    .OrderBy(t => t.TurnoQa));

string Arbol(Proyecto proyecto, string? padre, string token, string? rol, int profundidad = 0)
{
    var html = new System.Text.StringBuilder();
    if (Proyectos(rol) && profundidad < 12)
    {
        string padreInput = $"""<input type="hidden" name="padre" value="{E(padre)}">""";
        html.Append(Form(token, "nodo", Id(proyecto.Id) + padreInput +
            Campo("nombre", padre is null ? "Nombre del módulo" : "Nombre de la subtarea"),
            padre is null ? "Agregar módulo" : "Agregar subtarea"));
    }
    foreach (var hijo in proyecto.Nodos.Where(n => n.Padre == padre))
    {
        html.Append("<ul><li>").Append(E(hijo.Nombre));
        html.Append(Arbol(proyecto, hijo.Id, token, rol, profundidad + 1));
        html.Append("</li></ul>");
    }
    return html.ToString();
}

app.MapGet("/", (HttpContext ctx, Microsoft.AspNetCore.Antiforgery.IAntiforgery anti) =>
{
    string token = anti.GetAndStoreTokens(ctx).RequestToken!;
    string? rol = ctx.Session.GetString("rol");
    string contenido;
    lock (gate)
    {
        if (rol is null)
        {
            contenido = "<h1>Acceso a DevTrack</h1><p>Selecciona un rol para la demostración académica.</p>" +
                Form(token, "entrar", Campo("nombre", "Nombre") + Lista("rol", "Rol", roles), "Entrar");
        }
        else
        {
            contenido = $"<h1>DevTrack</h1><p>{E(ctx.Session.GetString("nombre"))} · {E(rol)}</p>" +
                Form(token, "salir", "", "Cerrar sesión") + "<h2>Proyectos y seguimiento</h2>";
            if (Proyectos(rol))
                contenido += Form(token, "proyecto", Campo("nombre", "Nombre del proyecto"), "Crear proyecto");
            if (rol == "Administrador" && deshacer.TryPeek(out _))
                contenido += Form(token, "deshacer", "", "Deshacer último cambio");
            if (Calidad(rol))
            {
                var cola = ColaQa(data);
                contenido += "<section><h2>Cola de QA</h2><ol>" +
                    string.Concat(cola.Select(t => "<li>" + E(t.Nombre) + "</li>")) + "</ol>";
                if (cola.TryPeek(out var siguiente))
                    contenido += Form(token, "qa", Id(siguiente.Id) +
                        Lista("resultado", "Resultado de QA", ["Aprobada", "Rechazada", "Error reportado"]) +
                        """<label>Observación<input name="observacion" maxlength="140"></label>""", "Revisar primera tarea");
                else contenido += "<p>No hay tareas en revisión.</p>";
                contenido += "</section>";
            }
            var historial = new LinkedList<Registro>(data.Historial);
            contenido += "<details><summary>Historial de modificaciones</summary><ol>";
            for (var nodo = historial.Last; nodo != null; nodo = nodo.Previous)
            {
                var entrada = nodo.Value;
                contenido += $"<li>{entrada.Fecha.ToLocalTime():g} · {E(entrada.Usuario)} · {E(entrada.Accion)}</li>";
            }
            contenido += "</ol></details>";
            foreach (var p in data.Proyectos)
            {
                var tareas = data.Tareas.Where(t => t.Proyecto == p.Id).ToList();
                int completas = tareas.Count(t => t.Estado == estados[3]);
                contenido += $"<section><h3>{E(p.Nombre)}</h3><p>{completas} completadas · {tareas.Count - completas} pendientes</p><progress value='{completas}' max='{Math.Max(1, tareas.Count)}'></progress>";
                if (Proyectos(rol))
                {
                    contenido += Form(token, "proyecto", Id(p.Id) + Campo("nombre", "Nombre", p.Nombre), "Guardar proyecto");
                    contenido += Form(token, "tarea", Id(p.Id) + Campo("nombre", "Título") + Campo("responsable", "Responsable") +
                        Lista("prioridad", "Prioridad", prioridades) + Campo("fecha", "Fecha de entrega", "", "date"), "Crear tarea");
                }
                contenido += "<details><summary>Módulos y subtareas</summary>" + Arbol(p, null, token, rol) + "</details>";
                foreach (var t in tareas.OrderBy(t => Array.IndexOf(prioridades, t.Prioridad)).ThenBy(t => t.Fecha))
                {
                    contenido += $"<article><strong>{E(t.Nombre)}</strong><p>{E(t.Responsable)} · {E(t.Prioridad)} · {E(t.Fecha)} · {E(t.Estado)}</p>";
                    if (!string.IsNullOrEmpty(t.Observacion))
                        contenido += $"<p>QA · {E(t.Observacion)}</p>";
                    if (Editar(rol))
                        contenido += Form(token, "editarTarea", Id(t.Id) + Campo("nombre", "Título", t.Nombre) + Campo("responsable", "Responsable", t.Responsable) +
                            Lista("prioridad", "Prioridad", prioridades, t.Prioridad) + Campo("fecha", "Fecha de entrega", t.Fecha, "date") +
                            Lista("estado", "Estado", new[] { t.Estado }.Concat(estados.Take(3)).Distinct(), t.Estado), "Guardar tarea");
                    contenido += "</article>";
                }
                contenido += "</section>";
            }
        }
    }
    var html = $$"""
        <!doctype html><html lang="es"><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
        <title>DevTrack</title><style>
        body{margin:auto;padding:2rem 1rem;max-width:64rem;background:#f1f5f9;color:#172c46;font:1rem system-ui}
        h1{font-size:2.4rem;margin:.4rem 0} section,article{padding:1rem;border:1px solid #c8d5e3;border-radius:10px;margin:1rem 0;background:white}
        form{display:flex;flex-wrap:wrap;align-items:end;gap:.65rem;margin:.85rem 0} label{display:grid;gap:.25rem}
        input,select,button{font:inherit;padding:.55rem;border:1px solid #7890a5;border-radius:5px} button{background:#18447c;color:white;cursor:pointer}
        </style><main>{{contenido}}</main></html>
        """;
    return Results.Content(html, "text/html; charset=utf-8");
});

app.MapPost("/", async (HttpContext ctx, Microsoft.AspNetCore.Antiforgery.IAntiforgery anti) =>
{
    if (!await anti.IsRequestValidAsync(ctx))
        return Results.BadRequest("Recarga el formulario y vuelve a intentarlo.");
    var f = await ctx.Request.ReadFormAsync();
    string V(string clave) => f[clave].ToString().Trim();
    string accion = V("accion"), nombre = V("nombre"), id = V("id");
    string? rol = ctx.Session.GetString("rol");
    if (accion == "entrar" && roles.Contains(V("rol")) && nombre.Length is > 0 and <= 140)
    {
        ctx.Session.SetString("rol", V("rol"));
        ctx.Session.SetString("nombre", nombre);
        return Results.Redirect("/");
    }
    if (accion == "salir")
    {
        ctx.Session.Clear();
        return Results.Redirect("/");
    }
    if (rol is null)
        return Results.Redirect("/");
    lock (gate)
    {
        // Se modifica una copia para conservar los datos anteriores si falla la escritura.
        bool esDeshacer = accion == "deshacer" && rol == "Administrador";
        Datos origen = data;
        if (esDeshacer && !deshacer.TryPeek(out origen!))
            return Results.BadRequest("No hay cambios para deshacer.");
        var cambios = JsonSerializer.Deserialize<Datos>(JsonSerializer.Serialize(origen))!;
        var proyecto = cambios.Proyectos.Find(p => p.Id == id);
        var tarea = cambios.Tareas.Find(t => t.Id == id);
        bool camposTarea = nombre.Length is > 0 and <= 140 && V("responsable").Length is > 0 and <= 140 &&
            prioridades.Contains(V("prioridad")) && DateOnly.TryParseExact(V("fecha"), "yyyy-MM-dd", out _);
        switch (accion)
        {
            case "deshacer" when esDeshacer:
                break;
            case "nodo" when Proyectos(rol) && proyecto is not null && nombre.Length is > 0 and <= 140:
                string? padre = V("padre") is "" ? null : V("padre");
                var ancestro = padre;
                int nivel = 0;
                while (ancestro != null)
                {
                    var nodo = proyecto.Nodos.Find(n => n.Id == ancestro);
                    if (nodo is null || ++nivel >= 12)
                        return Results.BadRequest("El nodo padre no es válido o se alcanzó el límite de niveles.");
                    ancestro = nodo.Padre;
                }
                proyecto.Nodos.Add(new(Guid.NewGuid().ToString("N"), nombre, padre));
                break;
            case "proyecto" when Proyectos(rol) && nombre.Length is > 0 and <= 140:
                if (id == "") cambios.Proyectos.Add(new(Guid.NewGuid().ToString("N"), nombre));
                else if (proyecto is not null) proyecto.Nombre = nombre;
                else return Results.NotFound();
                break;
            case "tarea" when Proyectos(rol) && proyecto is not null && camposTarea:
                cambios.Tareas.Add(new(Guid.NewGuid().ToString("N"), proyecto.Id, nombre, V("responsable"), V("prioridad"), V("fecha")));
                break;
            case "editarTarea" when Editar(rol) && tarea is not null && camposTarea:
                if (V("estado") != tarea.Estado && !estados.Take(3).Contains(V("estado")))
                    return Results.BadRequest("Estado no permitido.");
                if (tarea.Estado != V("estado")) tarea.Observacion = "";
                if (tarea.Estado != estados[2] && V("estado") == estados[2])
                    tarea.TurnoQa = cambios.Tareas.Max(t => t.TurnoQa) + 1;
                tarea.Estado = V("estado");
                tarea.Nombre = nombre;
                tarea.Responsable = V("responsable");
                tarea.Prioridad = V("prioridad");
                tarea.Fecha = V("fecha");
                break;
            case "qa" when Calidad(rol) && tarea?.Estado == estados[2]:
                var cola = ColaQa(cambios);
                if (!cola.TryDequeue(out var primera) || primera.Id != tarea.Id)
                    return Results.Conflict("La cola cambió. Recarga y revisa la primera tarea.");
                tarea = primera;
                string resultado = V("resultado"), observacion = V("observacion");
                if (resultado is not ("Aprobada" or "Rechazada" or "Error reportado") ||
                    observacion.Length > 140 || (resultado != "Aprobada" && observacion.Length == 0))
                    return Results.BadRequest("Escribe una observación para rechazar o reportar un error.");
                tarea.Estado = resultado == "Aprobada" ? estados[3] : estados[4];
                tarea.Observacion = resultado + (observacion.Length > 0 ? " · " + observacion : "");
                break;
            default:
                return Results.BadRequest("Revisa los datos y los permisos de tu rol.");
        }
        var historial = new LinkedList<Registro>(data.Historial);
        string detalle = accion switch
        {
            "proyecto" => "Proyecto guardado: " + nombre,
            "tarea" => "Tarea creada: " + nombre,
            "editarTarea" => "Tarea actualizada: " + nombre,
            "nodo" => "Nodo agregado: " + nombre,
            "qa" => "Revisión de " + tarea!.Nombre + ": " + V("resultado"),
            _ => "Se deshizo el último cambio"
        };
        historial.AddLast(new Registro(DateTimeOffset.UtcNow, ctx.Session.GetString("nombre") ?? rol, detalle));
        cambios.Historial = historial.ToList();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(cambios));
            File.Move(path + ".tmp", path, true);
            if (esDeshacer) deshacer.Pop();
            else deshacer.Push(data);
            data = cambios;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Results.Problem("No se pudo guardar el cambio.");
        }
    }
    return Results.Redirect("/");
});
app.Run();

sealed class Datos
{
    public List<Registro> Historial { get; set; } = [];
    public List<Proyecto> Proyectos { get; set; } = [];
    public List<Tarea> Tareas { get; set; } = [];
}
sealed record Registro(DateTimeOffset Fecha, string Usuario, string Accion);
sealed record Nodo(string Id, string Nombre, string? Padre);
sealed class Proyecto(string id, string nombre)
{
    public string Id { get; set; } = id;
    public List<Nodo> Nodos { get; set; } = [];
    public string Nombre { get; set; } = nombre;
}
sealed class Tarea(string id, string proyecto, string nombre, string responsable, string prioridad, string fecha)
{
    public string Id { get; set; } = id;
    public string Proyecto { get; set; } = proyecto;
    public string Nombre { get; set; } = nombre;
    public string Responsable { get; set; } = responsable;
    public string Prioridad { get; set; } = prioridad;
    public string Fecha { get; set; } = fecha;
    public long TurnoQa { get; set; }
    public string Estado { get; set; } = "Pendiente";
    public string Observacion { get; set; } = "";
}

