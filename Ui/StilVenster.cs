namespace WorkManager;

/// <summary>
/// Venster dat bij <see cref="Form.Show()"/> de focus níét afpakt. De stille sessies
/// (Teams, Outlook, WhatsApp, Smartschool, Albert Heijn, ISPnext) draaien in een venster
/// buiten beeld op (-4000, -4000); een gewone Form activeert zichzelf bij Show(), en dan
/// verliest het programma waar je op dat moment in typt de focus — zonder dat je ziet
/// waarheen. Dat is precies het "WorkManager springt zomaar naar voren"-gevoel: elke keer
/// dat zo'n sessie (na een crash of een vastloper) vers opgebouwd wordt, wipt het
/// onzichtbare venster naar de voorgrond.
///
/// ShowWithoutActivation raakt alleen Show(): een bewuste <see cref="Form.Activate"/> —
/// zoals bij de MFA-aanmelding of de 🗂 Archief-knop, waar het venster juist vooraan hoort —
/// werkt gewoon.
/// </summary>
public class StilVenster : Form
{
    protected override bool ShowWithoutActivation => true;
}
