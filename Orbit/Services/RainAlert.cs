using System;
using System.Linq;

namespace Orbit.Services;

// "Vai chover": avisa uma vez por chuva — quando não está chovendo e a previsão mostra chuva na próxima
// hora. Só volta a avisar depois de o tempo firmar (sem chuva agora nem na próxima hora). Puro, testável.
public sealed class RainAlert
{
    public const double WetMm = 0.1; // abaixo disso é garoa de medição, não chuva

    private bool _armed = true; // pode avisar (tempo firme desde o último aviso)

    // Minutos até a chuva (múltiplo de 15) quando deve avisar agora; null = não avisa.
    public int? Next(RainForecast f)
    {
        bool rainingNow = f.NowMm >= WetMm;
        int idx = Array.FindIndex(f.Next15.Take(4).ToArray(), mm => mm >= WetMm); // próximos 60 min
        if (!rainingNow && idx < 0) { _armed = true; return null; }   // firme: rearma
        if (rainingNow || !_armed) { _armed = false; return null; }   // já chove ou já avisou
        _armed = false;
        return idx == 0 ? 15 : idx * 15; // bloco i = a partir de ~i×15 min; bloco 0 = em instantes
    }

    public static string Text(int minutes) => minutes <= 15 ? "Chuva chegando em instantes" : $"Chuva em ~{minutes} min";
}
