using System.Security.Claims;
using Emir.Api.Data;
using Emir.Api.Domain;

namespace Emir.Api.Endpoints;

public record AddStreetRequest(string? Place, string? Street, string? Reason);
public record AddHouseRequest(string? Place, string? Street, string? House, string? Reason);

/// <summary>Выбор адреса каскадом Город → Улица → Дом и добавление улицы или дома прямо из формы адреса (как кнопка «Создать» в старой системе).</summary>
public static class AddressEndpoints
{
    public static void MapAddresses(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/addresses").RequireAuthorization();

        group.MapGet("/cities", (EmirDb db) => Addresses.CityList(db));
        group.MapGet("/streets", (string place, EmirDb db) => Addresses.StreetList(db, place.Trim()));
        group.MapGet("/houses", (string place, string street, EmirDb db) => Addresses.HouseList(db, place.Trim(), street.Trim()));

        // Новые улицы и дома заводит тот, кто ведёт справочники и регистрирует абонентов
        group.MapPost("/streets", async (AddStreetRequest request, EmirDb db, ClaimsPrincipal user) =>
        {
            await Addresses.AddStreet(db, user, request.Place, request.Street, request.Reason);
            return Results.NoContent();
        }).Roles(Roles.Service, Roles.Accountant, Roles.Admin);

        group.MapPost("/houses", async (AddHouseRequest request, EmirDb db, ClaimsPrincipal user) =>
        {
            await Addresses.AddHouse(db, user, request.Place, request.Street, request.House, request.Reason);
            return Results.NoContent();
        }).Roles(Roles.Service, Roles.Accountant, Roles.Admin);
    }
}
