using System;
using System.Security.Cryptography;
using System.Text;

namespace TPVC.Services;

/// <summary>
/// Identificadores del protocolo: un SyncId viaja (y entra a un UUIDv5) en minúsculas con guiones. EF Core
/// los guarda en mayúsculas en SQLite; da lo mismo, porque aquí se trabaja con <see cref="Guid"/> y se formatea
/// con <see cref="Format"/>. Los vectores de <c>contracts/golden/uuid5.json</c> salen de aquí.
/// </summary>
public static class SyncIds
{
    /// <summary>Espacio de nombres DNS de RFC 4122 (apéndice C).</summary>
    public static readonly Guid DnsNamespace = new("6ba7b810-9dad-11d1-80b4-00c04fd430c8");

    /// <summary>Espacio de nombres URL de RFC 4122 (apéndice C).</summary>
    public static readonly Guid UrlNamespace = new("6ba7b811-9dad-11d1-80b4-00c04fd430c8");

    /// <summary>
    /// Espacio de nombres de TPVC para los SyncId deterministas (catálogo sembrado en 8.8, renglones de las hojas
    /// en 8.18, devoluciones y saldos iniciales en 8.19–8.20): UUIDv5(URL, "https://github.com/Marioalr/TPVC/sync").
    /// No cambia nunca.
    /// </summary>
    public static readonly Guid TpvcNamespace = Uuid5(UrlNamespace, "https://github.com/Marioalr/TPVC/sync");

    /// <summary>GUID canónico del protocolo: minúsculas con guiones (igual en Windows y Android).</summary>
    public static string Format(Guid id) => id.ToString("D");

    /// <summary>UUID versión 5 (SHA-1) de RFC 4122: el nombre se codifica en UTF-8.</summary>
    public static Guid Uuid5(Guid namespaceId, string name)
    {
        Span<byte> ns = stackalloc byte[16];
        namespaceId.TryWriteBytes(ns, bigEndian: true, out _);

        var nameBytes = Encoding.UTF8.GetBytes(name);
        var input = new byte[16 + nameBytes.Length];
        ns.CopyTo(input);
        nameBytes.CopyTo(input, 16);

        Span<byte> hash = stackalloc byte[20];
        SHA1.HashData(input, hash);
        var bytes = hash[..16];
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x50); // versión 5
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80); // variante RFC 4122
        return new Guid(bytes, bigEndian: true);
    }

    /// <summary>
    /// SyncId del catálogo que siembra una base nueva (paso 8.8): UUIDv5 en el espacio de TPVC de "Category:&lt;nombre&gt;",
    /// "ClinicalService:&lt;código&gt;" o "ServiceDefaultProduct:&lt;código del servicio&gt;|&lt;código del producto&gt;". Dos equipos
    /// que siembran dan los mismos SyncId, así que al sincronizar no se duplica nada.
    /// </summary>
    public static Guid Seeded(string entityType, string key) => Uuid5(TpvcNamespace, $"{entityType}:{key}");

    /// <summary>
    /// UUIDv5 en el espacio de TPVC de un nombre que incluye SyncId: cada SyncId entra en minúsculas, así un
    /// SyncId leído en mayúsculas de la base local da lo mismo.
    /// </summary>
    public static Guid Derived(string prefix, params Guid[] syncIds) =>
        Uuid5(TpvcNamespace, prefix + string.Join(":", Array.ConvertAll(syncIds, Format)));
}
