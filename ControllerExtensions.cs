using Microsoft.AspNetCore.Mvc;

using PMMovil.Api.Models;

namespace PMMovil.Api;

public static class ControllerExtensions
{
    public static IActionResult ApiOk(this ControllerBase controller, string? message = null)
    {
        return controller.Ok(ApiResponse.Ok(message));
    }

    public static IActionResult ApiOk<T>(this ControllerBase controller, T data, string? message = null)
    {
        return controller.Ok(ApiResponse<T>.Ok(data, message));
    }

    public static IActionResult ApiError(this ControllerBase controller, string message, int statusCode = 400)
    {
        return controller.StatusCode(statusCode, ApiResponse.Error(message, statusCode));
    }

    public static IActionResult ApiNotFound(this ControllerBase controller, string message = "Recurso no encontrado")
    {
        return controller.NotFound(ApiResponse.NotFound(message));
    }

    public static IActionResult ApiServerError(this ControllerBase controller, string? message = null)
    {
        return controller.StatusCode(500, ApiResponse.ServerError(message));
    }
}
