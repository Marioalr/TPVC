using System;
using System.Text.RegularExpressions;
using TPVC.Helpers;
using TPVC.Services.Rules;
using TPVC.ViewModels.Patients;

namespace TPVC.Tests;

/// <summary>
/// Fase 7 (7.1): fecha de nacimiento con las diagonales solas. Las reglas de la máscara (sin WPF):
/// escribir, borrar y pegar sobre el texto y el cursor del cuadro. La validación sigue siendo la de
/// PatientFormRules.TryParseBirthDate, que no cambia.
/// </summary>
[TestClass]
public class DateMaskTests
{
    private static readonly DateTime Today = new(2026, 10, 9);

    /// <summary>Teclea cada carácter en el cursor (sin selección), como el usuario.</summary>
    private static DateMaskText.Edit TypeKeys(string keys, string text = "", int? caret = null)
    {
        var state = new DateMaskText.Edit(text, caret ?? text.Length);
        foreach (var key in keys)
        {
            state = DateMaskText.Insert(state.Text, state.Caret, 0, key.ToString()) ?? state;
        }
        return state;
    }

    private static DateMaskText.Edit PressBackspace(DateMaskText.Edit state) =>
        DateMaskText.Backspace(state.Text, state.Caret, 0) ?? state;

    // ------------------------------------------------------------------ escribir

    [TestMethod]
    public void TypingDigits_AddsTheSlashesAsTheDayAndMonthAreCompleted()
    {
        var expected = new (char Key, string Text, int Caret)[]
        {
            ('0', "0", 1), ('8', "08/", 3), ('0', "08/0", 4), ('8', "08/08/", 6),
            ('2', "08/08/2", 7), ('0', "08/08/20", 8), ('0', "08/08/200", 9), ('8', "08/08/2008", 10)
        };
        var state = new DateMaskText.Edit(string.Empty, 0);
        foreach (var (key, text, caret) in expected)
        {
            state = DateMaskText.Insert(state.Text, state.Caret, 0, key.ToString())!.Value;
            Assert.AreEqual(new DateMaskText.Edit(text, caret), state, $"Tras teclear '{key}'");
        }

        Assert.AreEqual(new DateTime(2008, 8, 8), PatientFormRules.TryParseBirthDate(state.Text, Today), "El ViewModel lo lee como siempre");
        Assert.IsNull(DateMaskText.Insert(state.Text, state.Caret, 0, "1"), "Lleno (dd/mm/aaaa): un dígito más no entra");
    }

    [TestMethod]
    public void TypingTheSlashesByHand_StillWorks_AndTheExtraSlashIsSwallowed()
    {
        Assert.AreEqual("8/8/2008", TypeKeys("8/8/2008").Text, "Día y mes de un dígito con su diagonal");
        Assert.AreEqual("08/08/2008", TypeKeys("08/08/2008").Text, "La diagonal tecleada tras la automática no se duplica");
        Assert.IsNull(DateMaskText.Insert("08/", 3, 0, "/"), "Diagonal tras diagonal: nada");
        Assert.IsNull(DateMaskText.Insert(string.Empty, 0, 0, "/"), "Diagonal sin día: nada");
        Assert.IsNull(DateMaskText.Insert("08/08/2008", 10, 0, "/"), "Una tercera diagonal: nada");
        Assert.AreEqual(new DateMaskText.Edit("08/", 3), DateMaskText.Insert("08/", 2, 0, "/"), "Antes de la diagonal automática: el cursor pasa sobre ella");
        Assert.AreEqual("8/8/2008", TypeKeys("8-8.2008").Text, "Guion y punto cuentan como diagonal");
    }

    [TestMethod]
    public void OnlyDigitsAndSlashes_AreAccepted()
    {
        foreach (var key in new[] { "a", "Z", " ", "ñ", ",", "+", "%" })
        {
            Assert.IsNull(DateMaskText.Insert("08/", 3, 0, key), $"\"{key}\" no entra");
        }
        Assert.AreEqual("08/08/2008", TypeKeys("08a08b2008").Text, "Las letras se ignoran sin estorbar");
    }

    [TestMethod]
    public void TypingInsideAFullDayOrMonth_IsIgnored_AndReplacingASelectionWorks()
    {
        Assert.IsNull(DateMaskText.Insert("08/08/2008", 1, 0, "5"), "El día ya tiene dos dígitos y su diagonal");
        Assert.IsNull(DateMaskText.Insert("08/08/2008", 4, 0, "5"), "El mes ya tiene dos dígitos y su diagonal");
        Assert.IsNull(DateMaskText.Insert("08/08/2008", 7, 0, "5"), "El año ya tiene cuatro dígitos");

        Assert.AreEqual(new DateMaskText.Edit("09/08/2008", 3), DateMaskText.Insert("08/08/2008", 1, 1, "9"), "Reemplazar un dígito del día: el cursor pasa al mes");
        Assert.AreEqual(new DateMaskText.Edit("08/1/2008", 4), DateMaskText.Insert("08/08/2008", 3, 2, "1"), "Reemplazar el mes: queda de un dígito");
        var month = DateMaskText.Insert("08/1/2008", 4, 0, "2")!.Value;
        Assert.AreEqual(new DateMaskText.Edit("08/12/2008", 6), month, "Con el segundo dígito del mes el cursor pasa al año");
        Assert.AreEqual(new DateMaskText.Edit("1", 1), DateMaskText.Insert("08/08/2008", 0, 10, "1"), "Todo seleccionado: se empieza de nuevo");
        Assert.AreEqual(new DateMaskText.Edit("14/03/", 6), DateMaskText.Paste("08/08/2008", 0, 10, "1403"));
    }

    // ------------------------------------------------------------------ borrar

    [TestMethod]
    public void BackspaceFromTheEnd_RemovesTheSlashesByItself()
    {
        var expected = new[] { "08/08/200", "08/08/20", "08/08/2", "08/08/", "08/0", "08/", "0", "" };
        var state = new DateMaskText.Edit("08/08/2008", 10);
        foreach (var text in expected)
        {
            state = DateMaskText.Backspace(state.Text, state.Caret, 0)!.Value;
            Assert.AreEqual(new DateMaskText.Edit(text, text.Length), state);
        }
        Assert.IsNull(DateMaskText.Backspace(string.Empty, 0, 0), "Vacío: nada que borrar");

        Assert.AreEqual(new DateMaskText.Edit(string.Empty, 0), PressBackspace(new DateMaskText.Edit("8/", 2)), "La diagonal escrita a mano también se va con su dígito");
        Assert.AreEqual(new DateMaskText.Edit("8/", 2), PressBackspace(new DateMaskText.Edit("8/8/", 4)));
    }

    [TestMethod]
    public void BackspaceAfterASlashInTheMiddle_RemovesTheDigitBefore_AndKeepsTheRestInPlace()
    {
        // 08/08/|2008: se borra el 8 del mes y el año no se recorre
        var state = PressBackspace(new DateMaskText.Edit("08/08/2008", 6));
        Assert.AreEqual(new DateMaskText.Edit("08/0/2008", 4), state);
        state = DateMaskText.Insert(state.Text, state.Caret, 0, "9")!.Value;
        Assert.AreEqual(new DateMaskText.Edit("08/09/2008", 6), state, "Al corregir el mes el cursor sigue al año");

        Assert.AreEqual(new DateMaskText.Edit("0/08/2008", 1), PressBackspace(new DateMaskText.Edit("08/08/2008", 3)));
        Assert.AreEqual(new DateMaskText.Edit("0/08/2008", 1), PressBackspace(new DateMaskText.Edit("08/08/2008", 2)), "Antes de la diagonal: el dígito");
        Assert.AreEqual(new DateMaskText.Edit("08/08/208", 8), PressBackspace(new DateMaskText.Edit("08/08/2008", 9)), "Dentro del año");
    }

    [TestMethod]
    public void Delete_RemovesForward_AndASelectionLeavesItsPartEmpty()
    {
        Assert.AreEqual(new DateMaskText.Edit("08/8/2008", 3), DateMaskText.Delete("08/08/2008", 2, 0), "Antes de una diagonal: el dígito que la sigue");
        Assert.AreEqual(new DateMaskText.Edit("08", 2), DateMaskText.Delete("08/", 2, 0), "La diagonal del final");
        Assert.AreEqual(new DateMaskText.Edit("8/08/2008", 0), DateMaskText.Delete("08/08/2008", 0, 0));
        Assert.IsNull(DateMaskText.Delete("08/08/2008", 10, 0), "Al final: nada que borrar");

        var emptyMonth = DateMaskText.Delete("08/08/2008", 3, 2)!.Value;
        Assert.AreEqual(new DateMaskText.Edit("08//2008", 3), emptyMonth, "Sin el mes, el año se queda donde está");
        Assert.AreEqual("08/12/2008", TypeKeys("12", emptyMonth.Text, emptyMonth.Caret).Text, "Y se escribe el mes de nuevo");

        Assert.AreEqual(new DateMaskText.Edit(string.Empty, 0), DateMaskText.Backspace("08/08/2008", 0, 10), "Todo seleccionado");
        Assert.AreEqual("08/0", TypeKeys("0", "08").Text, "Tras quitar la diagonal final, escribir la repone");
    }

    // ------------------------------------------------------------------ pegar

    [TestMethod]
    public void Pasting_TheThreeForms_KeepsWorking()
    {
        foreach (var (pasted, expected) in new[]
        {
            ("08/08/2008", "08/08/2008"),
            ("8/8/2008", "8/8/2008"),
            ("08082008", "08/08/2008"),
            ("  08082008 ", "08/08/2008"),
            ("08-08-2008", "08/08/2008"),
            ("2008-08-08", "08/08/2008"),
            ("8/8/08", "8/8/08")
        })
        {
            var edit = DateMaskText.Paste(string.Empty, 0, 0, pasted);
            Assert.AreEqual(new DateMaskText.Edit(expected, expected.Length), edit, $"Pegar \"{pasted}\"");
            Assert.AreEqual(PatientFormRules.TryParseBirthDate(pasted, Today), PatientFormRules.TryParseBirthDate(edit!.Value.Text, Today),
                $"\"{pasted}\" se lee igual que antes");
        }

        Assert.AreEqual(new DateMaskText.Edit("08/08/2008", 10), DateMaskText.Paste("01/01/1990", 0, 10, "08082008"), "Sobre todo lo seleccionado");
        Assert.IsNull(DateMaskText.Paste("08/08/2008", 10, 0, "08082008"), "No cabe");
        Assert.IsNull(DateMaskText.Paste(string.Empty, 0, 0, "080820081"), "Más de dd/mm/aaaa");
        Assert.IsNull(DateMaskText.Paste(string.Empty, 0, 0, "hoy"), "Sin dígitos");
        Assert.IsNull(DateMaskText.Paste(string.Empty, 0, 0, null));
    }

    [TestMethod]
    public void PartialInput_ValidatesExactlyAsTheViewModelDoes()
    {
        // Lo que se ve al ir tecleando 08082008: incompleto no es fecha; con año de dos dígitos sí
        // (dd/MM/yy, como si se hubiera tecleado así antes)
        Assert.IsNull(PatientFormRules.TryParseBirthDate("08/", Today));
        Assert.IsNull(PatientFormRules.TryParseBirthDate("08/08/", Today));
        Assert.IsNull(PatientFormRules.TryParseBirthDate("08/08/2", Today));
        Assert.AreEqual(new DateTime(2020, 8, 8), PatientFormRules.TryParseBirthDate("08/08/20", Today));
        Assert.IsNull(PatientFormRules.TryParseBirthDate("08/08/200", Today));
        Assert.AreEqual(new DateTime(2008, 8, 8), PatientFormRules.TryParseBirthDate("08/08/2008", Today));
        Assert.IsNull(PatientFormRules.TryParseBirthDate(TypeKeys("31022008").Text, Today), "31/02/2008 sigue sin ser fecha");
        Assert.IsNull(PatientFormRules.TryParseBirthDate(TypeKeys("01012030").Text, Today), "Futura: sigue sin valer");
    }

    // ------------------------------------------------------------------ nunca se sale del formato

    [TestMethod]
    public void AnyKeySequence_StaysWithinDdMmYyyy()
    {
        var shape = new Regex(@"^\d{0,2}(/\d{0,2}(/\d{0,4})?)?$");
        var random = new Random(7);
        var keys = "0123456789/-.a ".ToCharArray();
        for (int run = 0; run < 400; run++)
        {
            var state = new DateMaskText.Edit(string.Empty, 0);
            for (int step = 0; step < 30; step++)
            {
                int start = random.Next(state.Text.Length + 1);
                int length = random.Next(4) == 0 ? random.Next(state.Text.Length - start + 1) : 0;
                if (random.Next(5) > 0) start = state.Caret;
                var history = $"{state.Text}|{start},{length}";
                state = random.Next(6) switch
                {
                    0 => DateMaskText.Backspace(state.Text, start, length),
                    1 => DateMaskText.Delete(state.Text, start, length),
                    2 => DateMaskText.Paste(state.Text, start, length, random.Next(2) == 0 ? "08082008" : "8/8/2008"),
                    _ => DateMaskText.Insert(state.Text, start, length, keys[random.Next(keys.Length)].ToString())
                } ?? state;

                Assert.IsLessThanOrEqualTo(DateMaskText.MaxLength, state.Text.Length, $"Demasiado largo tras {history}: \"{state.Text}\"");
                Assert.IsTrue(shape.IsMatch(state.Text), $"Fuera de dd/mm/aaaa tras {history}: \"{state.Text}\"");
                Assert.IsTrue(state.Caret >= 0 && state.Caret <= state.Text.Length, $"Cursor fuera del texto tras {history}");
            }
        }
    }
}
