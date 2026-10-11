using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TPVC.Data;
using TPVC.Models;
using TPVC.Services;
using TPVC.ViewModels;

namespace TPVC.Tests;

/// <summary>
/// «Ingresar pedido» (v1.22.0): leer el correo del proveedor, emparejar con el catálogo e ingresar todo junto. El pedido
/// de ejemplo son los renglones de un correo real de LUIN (#5175), sin los datos del cliente.
/// </summary>
[TestClass]
[DoNotParallelize]
public class SupplierOrderTests
{
    private static readonly (string Desc, string Sku, int Qty, string Total)[] LuinOrder =
    {
        ("VALCLAN 875/125MG C/14 TABS", "WAN014", 2, "$145.02"),
        ("BUTIMAXIL 500MG C/20 CAPS", "BRU006", 1, "$53.07"),
        ("CEFALVER 500MG C/20 CAPS", "MVR037", 2, "$89.02"),
        ("VOYDOL S 30MG C/4 TABS", "RAM013", 3, "$30.78"),
        ("VOYDOL 10MG C/10 TABS", "RAM030", 4, "$29.24"),
        ("DICLOFENACO 100MG C/20 TABS", "AVI016", 3, "$21.87"),
        ("BIOBEND SOLUCION BUCAL 360ML", "BIO059", 1, "$40.53"),
        ("DUALGOS 325/200MG C/20 TABS", "LIF007", 3, "$105.51"),
        ("SUPRATEX JARABE 600MG/100 ML 120ML", "MAV001", 2, "$87.88"),
        ("ATORLIP 40MG C/10 TABS", "MAV033", 1, "$48.35"),
        ("ASA 100 100MG LP C/30 TABS", "LIO010", 1, "$48.66"),
        ("VYLKOR 8MG C/10 TABS", "RAM009", 3, "$193.26"),
        ("RAAMCINET 10MG C/10 TABS", "RAM037", 3, "$64.29"),
        ("NAVONTEC INY 8MG C/3 AMP", "JAY043", 1, "$66.39"),
        ("RETIRIX SOLUCION 100MG/100ML 50ML", "BIO045", 2, "$78.66"),
        ("BIOSTAFEX 120MG C/10 TABS", "BIO012", 2, "$89.34"),
        ("ATROXOLAM JARABE 7/1.5MG/ML 150ML", "NOV013", 1, "$29.81"),
        ("BROMICOF SOLUCION INFANTIL", "LOE014", 1, "$16.74"),
        ("BROMICOF SOLUCION ADULTO", "LOE013", 1, "$17.20"),
        ("DOLPRIN SUSPENSION 2G/100 ML 120 ML", "COL057", 2, "$55.00"),
        ("DIRPASID 10MG C/20 TABS", "BRU031", 3, "$24.03"),
        ("FAZOLIN GOTAS OFTALMICAS 1MG 15ML", "COL064", 1, "$24.68"),
        ("RAAMFEN 25MG C/30 TABS", "RAM002", 2, "$33.76"),
    };

    /// <summary>El correo como queda al copiarlo: con tabuladores, una celda por línea o solo con espacios.</summary>
    private static string LuinEmail(string cellSeparator, string rowSeparator)
    {
        var sb = new StringBuilder();
        sb.Append("LUIN Distribuidora Farmacéutica").Append(rowSeparator);
        sb.Append("Detalles de la Compra").Append(rowSeparator);
        sb.Append("Número de Orden: #5175").Append(rowSeparator);
        sb.Append("Sucursal: Tecamachalco").Append(rowSeparator);
        sb.Append(string.Join(cellSeparator, "Producto", "SKU", "Cantidad", "Total")).Append(rowSeparator);
        foreach (var (desc, sku, qty, total) in LuinOrder)
        {
            sb.Append(string.Join(cellSeparator, desc, sku, qty.ToString(), total)).Append(rowSeparator);
        }
        sb.Append("Producto Sujeto a Existencia").Append(rowSeparator);
        sb.Append("Cliente: Consultorio de prueba").Append(rowSeparator);
        sb.Append("Tipo de Pago: Pago en efectivo").Append(rowSeparator);
        sb.Append("Resumen de la Compra").Append(rowSeparator);
        sb.Append(string.Join(cellSeparator, "Subtotal", "$1,393.09")).Append(rowSeparator);
        sb.Append(string.Join(cellSeparator, "Ahorrado", "$0.00")).Append(rowSeparator);
        sb.Append(string.Join(cellSeparator, "IVA", "$0.00")).Append(rowSeparator);
        sb.Append(string.Join(cellSeparator, "Total + IVA", "$1,393.09")).Append(rowSeparator);
        return sb.ToString();
    }

    private string _dbPath = string.Empty;
    private AppDbContext _context = null!;
    private InventoryService _inventory = null!;

    [TestInitialize]
    public void Setup()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"tpvc_pedido_{Guid.NewGuid():N}.db");
        _context = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={_dbPath}").Options);
        DatabaseInitializer.Initialize(_context, seedDemoData: false);
        _inventory = new InventoryService(_context);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try { _context?.Database.EnsureDeleted(); } catch { }
        _context?.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { File.Delete(_dbPath); } catch { }
    }

    private async Task<Product> AddProductAsync(string code, string name, string? supplierCode = null, int stock = 0, bool active = true)
    {
        var category = (await _inventory.GetCategoriesAsync()).First();
        await _inventory.SaveProductAsync(new Product
        {
            Code = code, Name = name, CategoryId = category.Id, Price = 50m, CurrentStock = stock, SupplierCode = supplierCode, IsActive = active
        });
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={_dbPath}").Options);
        return db.Products.AsNoTracking().Single(p => p.Code == code);
    }

    // ================================================================== leer el correo

    [TestMethod]
    [DataRow("\t", "\n", DisplayName = "Tabla copiada (tabuladores)")]
    [DataRow("\n", "\n", DisplayName = "Una celda por línea")]
    [DataRow(" ", "\n", DisplayName = "Solo espacios")]
    [DataRow("\t", "\r\n", DisplayName = "Tabuladores con CRLF")]
    public void TheLuinEmail_IsReadCompletely_InAnyLayout(string cellSeparator, string rowSeparator)
    {
        var order = SupplierOrderParser.Parse(LuinEmail(cellSeparator, rowSeparator));

        Assert.AreEqual("5175", order.OrderNumber);
        Assert.HasCount(LuinOrder.Length, order.Lines);
        Assert.AreEqual(1393.09m, order.Subtotal);
        Assert.AreEqual(0m, order.Tax);
        Assert.AreEqual(1393.09m, order.Total);
        Assert.AreEqual(1393.09m, order.LinesTotal);
        Assert.IsTrue(order.MatchesEmailTotal);

        for (int i = 0; i < LuinOrder.Length; i++)
        {
            var (desc, sku, qty, _) = LuinOrder[i];
            Assert.AreEqual(desc, order.Lines[i].Description, $"Renglón {i + 1}");
            Assert.AreEqual(sku, order.Lines[i].Sku);
            Assert.AreEqual(qty, order.Lines[i].Quantity);
        }
        Assert.AreEqual(72.51m, order.Lines[0].UnitCost, "VALCLAN: 2 por $145.02");
        Assert.AreEqual(10.26m, order.Lines[3].UnitCost, "VOYDOL S: 3 por $30.78");
    }

    [TestMethod]
    public void AnOrderWithAMissingLine_DoesNotMatchTheEmailSubtotal()
    {
        var email = LuinEmail("\t", "\n").Replace("BUTIMAXIL 500MG C/20 CAPS\tBRU006\t1\t$53.07\n", string.Empty);
        var order = SupplierOrderParser.Parse(email);
        Assert.HasCount(LuinOrder.Length - 1, order.Lines);
        Assert.IsFalse(order.MatchesEmailTotal);
    }

    [TestMethod]
    public void TextWithoutAnOrder_HasNoLines()
    {
        Assert.IsEmpty(SupplierOrderParser.Parse("Hola, le confirmo su pedido para mañana.").Lines);
        Assert.IsEmpty(SupplierOrderParser.Parse(null).Lines);
        Assert.IsNull(SupplierOrderParser.Parse("sin totales").MatchesEmailTotal);
    }

    [TestMethod]
    public void ThePresentation_IsReadFromTheDescription()
    {
        Assert.AreEqual("Caja con 14 tabletas", SupplierOrderParser.PresentationOf("VALCLAN 875/125MG C/14 TABS"));
        Assert.AreEqual("Caja con 20 cápsulas", SupplierOrderParser.PresentationOf("BUTIMAXIL 500MG C/20 CAPS"));
        Assert.AreEqual("Caja con 3 ampolletas", SupplierOrderParser.PresentationOf("NAVONTEC INY 8MG C/3 AMP"));
        Assert.IsNull(SupplierOrderParser.PresentationOf("BIOBEND SOLUCION BUCAL 360ML"));
    }

    // ================================================================== emparejar con el catálogo

    [TestMethod]
    public async Task EachLine_IsMatchedBySupplierCode_Code_Name_OrSimilarity()
    {
        var bySupplier = await AddProductAsync("MED-100", "Valclan (amoxicilina/clavulanato)", supplierCode: "WAN014");
        var byCode = await AddProductAsync("BRU006", "Butimaxil cápsulas");
        var byName = await AddProductAsync("MED-101", "Cefalver 500mg C/20 Caps");
        var similar = await AddProductAsync("MED-102", "Voydol 10 mg tabletas");

        var order = SupplierOrderParser.Parse(LuinEmail("\t", "\n"));
        var suggestions = await _inventory.MatchSupplierOrderAsync(order.Lines);
        SupplierLineSuggestion For(string sku) => suggestions.Single(s => s.Line.Sku == sku);

        Assert.AreEqual(SupplierLineMatch.SupplierCode, For("WAN014").Match);
        Assert.AreEqual(bySupplier.Id, For("WAN014").Product!.Id);
        Assert.AreEqual(SupplierLineMatch.ProductCode, For("BRU006").Match);
        Assert.AreEqual(byCode.Id, For("BRU006").Product!.Id);
        Assert.AreEqual(SupplierLineMatch.SameName, For("MVR037").Match);
        Assert.AreEqual(byName.Id, For("MVR037").Product!.Id);
        Assert.AreEqual(SupplierLineMatch.Similar, For("RAM030").Match, "Misma marca y misma concentración (10 mg)");
        Assert.AreEqual(similar.Id, For("RAM030").Product!.Id);
        Assert.AreEqual(SupplierLineMatch.New, For("RAM013").Match, "VOYDOL S 30 mg no es el Voydol de 10 mg");
        Assert.IsNull(For("RAM013").Product);
        Assert.AreEqual(SupplierLineMatch.New, For("AVI016").Match);
    }

    // ================================================================== ingresar

    [TestMethod]
    public async Task TheOrder_IsReceivedInOneGo_WithNewProducts_Costs_Batches_AndKardex()
    {
        var valclan = await AddProductAsync("MED-100", "Valclan", supplierCode: "WAN014", stock: 1);
        var category = (await _inventory.GetCategoriesAsync()).First(c => c.Name.Contains("Medicamentos"));
        var expiration = DateTime.Today.AddYears(1);

        var result = await _inventory.ReceiveSupplierOrderAsync(new SupplierReceipt
        {
            Supplier = "LUIN",
            OrderNumber = "5175",
            CategoryIdForNewProducts = category.Id,
            Lines = new List<SupplierReceiptLine>
            {
                new() { ProductId = valclan.Id, Sku = "WAN014", Description = "VALCLAN 875/125MG C/14 TABS", Quantity = 2, UnitCost = 72.51m, BatchNumber = "L2401", ExpirationDate = expiration },
                new() { ProductId = null, Sku = "BRU006", Description = "BUTIMAXIL 500MG C/20 CAPS", Quantity = 1, UnitCost = 50m },
            }
        });

        Assert.AreEqual(2, result.Received);
        CollectionAssert.AreEqual(new[] { "BUTIMAXIL 500MG C/20 CAPS" }, result.CreatedWithoutPrice.ToArray());

        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={_dbPath}").Options);
        var v = db.Products.AsNoTracking().Single(p => p.Id == valclan.Id);
        Assert.AreEqual(3, v.CurrentStock);
        Assert.AreEqual(72.51m, v.UnitCost, "El costo del renglón");
        var batch = db.ProductBatches.AsNoTracking().Single(b => b.ProductId == valclan.Id);
        Assert.AreEqual("L2401", batch.BatchNumber);
        Assert.AreEqual(expiration, batch.ExpirationDate);
        Assert.AreEqual(2, batch.Quantity);

        var created = db.Products.AsNoTracking().Single(p => p.SupplierCode == "BRU006");
        Assert.AreEqual("BRU006", created.Code, "El SKU como código");
        Assert.AreEqual("Caja con 20 cápsulas", created.Presentation);
        Assert.AreEqual("Cajas", created.Unit);
        Assert.AreEqual(category.Id, created.CategoryId);
        Assert.AreEqual(0m, created.Price, "Sin precio de venta: se completa después");
        Assert.AreEqual(50m, created.UnitCost);
        Assert.AreEqual(1, created.CurrentStock);
        Assert.AreEqual(0, created.MinStock, "Sin alertas hasta que se configure");

        var movements = db.InventoryMovements.AsNoTracking().Where(m => m.Reason == "Pedido LUIN #5175").ToList();
        Assert.HasCount(2, movements);
        Assert.IsTrue(movements.All(m => m.MovementType == MovementType.EntradaCompra));
        Assert.AreEqual(batch.Id, movements.Single(m => m.ProductId == valclan.Id).BatchId, "La entrada con lote queda ligada a él");
        Assert.AreEqual(1, movements.Single(m => m.ProductId == valclan.Id).StockBefore);

        // El mismo pedido ya se ingresó, y el siguiente empareja solo por el SKU que se guardó
        Assert.IsNotNull(await _inventory.WhenWasSupplierOrderReceivedAsync("LUIN", "5175"));
        Assert.IsNull(await _inventory.WhenWasSupplierOrderReceivedAsync("LUIN", "9999"));
        var next = await _inventory.MatchSupplierOrderAsync(new[] { new SupplierOrderLine("BUTIMAXIL 500MG C/20 CAPS", "BRU006", 1, 50m) });
        Assert.AreEqual(SupplierLineMatch.SupplierCode, next.Single().Match);
    }

    [TestMethod]
    public async Task ALineWithABatchButNoExpiration_StopsTheWholeOrder()
    {
        var category = (await _inventory.GetCategoriesAsync()).First();
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => _inventory.ReceiveSupplierOrderAsync(new SupplierReceipt
        {
            Supplier = "LUIN",
            OrderNumber = "1",
            CategoryIdForNewProducts = category.Id,
            Lines = new List<SupplierReceiptLine>
            {
                new() { Sku = "AAA001", Description = "PRIMERO", Quantity = 1, UnitCost = 1m },
                new() { Sku = "AAA002", Description = "SEGUNDO", Quantity = 1, UnitCost = 1m, BatchNumber = "L1" },
            }
        }));
        Assert.IsEmpty(await _inventory.GetProductsAsync(searchText: "AAA00"), "No quedó nada a medias");
    }

    [TestMethod]
    public async Task ANewProductWhoseSkuIsAnotherProductsCode_GetsANumberedCode()
    {
        await AddProductAsync("RAM013", "Otro producto con ese código");
        var category = (await _inventory.GetCategoriesAsync()).First();
        await _inventory.ReceiveSupplierOrderAsync(new SupplierReceipt
        {
            CategoryIdForNewProducts = category.Id,
            Lines = new List<SupplierReceiptLine> { new() { Sku = "RAM013", Description = "VOYDOL S 30MG C/4 TABS", Quantity = 3, UnitCost = 10.26m } }
        });
        var created = (await _inventory.GetProductsAsync(searchText: "VOYDOL")).Single();
        Assert.AreEqual("RAM013-2", created.Code);
        Assert.AreEqual("RAM013", created.SupplierCode);
    }

    // ================================================================== la hoja

    [TestMethod]
    public async Task TheSheet_ReadsTheEmail_LetsEditEachLine_AndReceivesIt()
    {
        var existing = await AddProductAsync("MED-100", "Valclan", supplierCode: "WAN014");
        var preferences = new LocalPreferencesStore(Path.Combine(Path.GetTempPath(), $"tpvc_pref_{Guid.NewGuid():N}.json"));
        var sheet = new SupplierOrderViewModel(_inventory, preferences) { Supplier = "LUIN" };
        string? received = null;
        bool closed = false;
        sheet.Received += m => received = m;
        sheet.Closed += () => closed = true;

        // Sin pedido en el texto
        sheet.PastedText = "nada";
        await sheet.ReadOrderAsync();
        Assert.IsFalse(sheet.IsReviewing);
        StringAssert.Contains(sheet.ErrorMessage, "No se encontraron productos");

        sheet.PastedText = LuinEmail("\t", "\n");
        await sheet.ReadOrderAsync();
        Assert.IsTrue(sheet.IsReviewing);
        Assert.AreEqual("Pedido #5175 · LUIN", sheet.Title);
        Assert.HasCount(LuinOrder.Length, sheet.Lines);
        Assert.IsTrue(sheet.IsTotalCheckOk);
        StringAssert.Contains(sheet.TotalCheckText, "igual que el correo");
        Assert.AreEqual("23 productos · 1 del catálogo · 22 nuevos", sheet.SummaryText);
        Assert.IsTrue(sheet.HasNewProducts);
        StringAssert.Contains(sheet.CategoryForNew!.Name, "Medicamentos");

        var valclan = sheet.Lines[0];
        Assert.AreEqual(existing.Id, valclan.SelectedProduct.ProductId);
        Assert.AreEqual("Llegó en un pedido anterior", valclan.MatchText);
        Assert.AreEqual(72.51m, valclan.UnitCost, "Precargado: total ÷ cantidad");
        Assert.IsTrue(sheet.CanConfirm);

        // Se corrige a mano: costo, lote sin caducidad (no deja confirmar) y luego con caducidad
        valclan.UnitCost = 70m;
        Assert.AreEqual("$140.00", valclan.LineTotalText);
        valclan.BatchNumber = "L2401";
        Assert.IsFalse(sheet.CanConfirm);
        StringAssert.Contains(valclan.Error, "caducidad");
        valclan.ExpirationText = "03/2027";
        Assert.AreEqual(string.Empty, valclan.Error);
        Assert.IsTrue(sheet.CanConfirm);

        // Un renglón que no se quiere crear se empareja con un producto existente
        var butimaxil = sheet.Lines[1];
        Assert.IsTrue(butimaxil.IsNewProduct);
        butimaxil.SelectedProduct = sheet.ProductChoices.Single(c => c.ProductId == existing.Id);
        Assert.AreEqual("Elegido a mano", butimaxil.MatchText);
        Assert.AreEqual("23 productos · 2 del catálogo · 21 nuevos", sheet.SummaryText);

        await sheet.ConfirmAsync();
        Assert.AreEqual(string.Empty, sheet.ErrorMessage);
        Assert.IsTrue(closed);
        StringAssert.Contains(received, "23 productos ingresados");
        StringAssert.Contains(received, "21 nuevos sin precio de venta");
        Assert.AreEqual("LUIN", preferences.Current.LastSupplierName, "El proveedor se propone la siguiente vez");

        var product = (await _inventory.GetProductsAsync(searchText: "MED-100")).Single();
        Assert.AreEqual(2 + 1, product.CurrentStock, "VALCLAN (2) y BUTIMAXIL (1) entraron al mismo producto");
        Assert.AreEqual(53.07m, product.UnitCost, "Queda el costo del último renglón que lo trae (BUTIMAXIL)");
        Assert.AreEqual(new DateTime(2027, 3, 31), (await _inventory.GetBatchesAsync(product.Id)).Single().ExpirationDate, "mm/aaaa: fin de mes");

        // Volver a abrir el mismo pedido avisa que ya se ingresó
        var again = new SupplierOrderViewModel(_inventory) { Supplier = "LUIN", PastedText = LuinEmail("\t", "\n") };
        await again.ReadOrderAsync();
        StringAssert.Contains(again.DuplicateWarning, "ya se ingresó");
        Assert.AreEqual(SupplierLineMatch.SupplierCode, again.Lines.Single(l => l.Sku == "RAM013").Match, "Los nuevos ya se emparejan por su SKU");
    }
}
