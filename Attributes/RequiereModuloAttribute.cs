using System;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using PMMovil.Api.Services;

namespace PMMovil.Api.Attributes;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
public class RequiereModuloAttribute : Attribute, IAsyncAuthorizationFilter
{
    private readonly int _moduloId;
    private readonly string? _accion;
    private readonly bool _reemplazaModuloPadre;
    private readonly int[] _modulosAlternativos;

    public RequiereModuloAttribute(int moduloId)
    {
        _moduloId = moduloId;
        _modulosAlternativos = Array.Empty<int>();
    }

    public RequiereModuloAttribute(int moduloId, string accion, bool reemplazaModuloPadre = false, params int[] modulosAlternativos)
    {
        _moduloId = moduloId;
        _accion = accion;
        _reemplazaModuloPadre = reemplazaModuloPadre;
        _modulosAlternativos = modulosAlternativos;
    }

    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        // Permite que una acción de un controlador con permiso general use un
        // módulo más específico. Caseta, por ejemplo, comparte el controlador
        // entre entradas (12) y salida de relación (165), pero no debe exigir
        // ambos permisos al mismo usuario.
        if (!_reemplazaModuloPadre && context.Filters
            .OfType<RequiereModuloAttribute>()
            .Any(filter =>
                !ReferenceEquals(filter, this)
                && filter._reemplazaModuloPadre))
        {
            return;
        }

        // Un atributo explícito en la acción sustituye la inferencia del
        // atributo de clase para el mismo módulo.
        if (_accion is null && context.Filters
            .OfType<RequiereModuloAttribute>()
            .Any(filter =>
                !ReferenceEquals(filter, this)
                && filter._moduloId == _moduloId
                && filter._accion is not null))
        {
            return;
        }

        var username = context.HttpContext.User.Identity?.Name;
        if (string.IsNullOrWhiteSpace(username))
        {
            context.Result = new UnauthorizedObjectResult(new
            {
                message = "No se pudo identificar al usuario."
            });
            return;
        }

        var permissionService = context.HttpContext.RequestServices
            .GetRequiredService<IModulePermissionService>();
        var action = _accion ?? InferAction(context.HttpContext.Request.Method);
        bool allowed = await permissionService.HasPermissionAsync(
            username,
            _moduloId,
            action,
            context.HttpContext.RequestAborted);

        foreach (int moduloAlternativo in _modulosAlternativos)
        {
            if (allowed) break;
            allowed = await permissionService.HasPermissionAsync(
                username,
                moduloAlternativo,
                action,
                context.HttpContext.RequestAborted);
        }

        if (!allowed)
            context.Result = new ForbidResult();
    }

    private static string InferAction(string httpMethod)
    {
        if (HttpMethods.IsPost(httpMethod)) return "Agregar";
        if (HttpMethods.IsPut(httpMethod) || HttpMethods.IsPatch(httpMethod)) return "Modificar";
        if (HttpMethods.IsDelete(httpMethod)) return "Borrar";
        return "Lectura";
    }
}
