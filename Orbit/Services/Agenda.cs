using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Ical.Net;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
using Serilog;

namespace Orbit.Services;

// Evento já no horário local. JoinUrl = link da reunião (Teams/Meet/Zoom) achado no local, URL ou descrição.
public sealed record AgendaEvent(string Title, DateTime Start, DateTime End, bool AllDay, string? JoinUrl)
{
    public string When => AllDay ? "Dia todo" : $"{Start:HH:mm}–{End:HH:mm}";
    public bool HasJoin => JoinUrl is not null;
    public string Key => $"{Title}|{Start:o}";
}

// Agenda por link iCal (Google: "endereço secreto"; Outlook/Teams: "publicar calendário"). Recorrência e
// fusos (IANA do Google, nomes do Windows do Outlook) ficam com o Ical.Net.
public static class Agenda
{
    private static readonly Regex Join = new(
        @"https://(?:[\w-]+\.)?(?:teams\.microsoft\.com/l/meetup-join|teams\.live\.com/meet|meet\.google\.com|zoom\.us/j)/[^\s""'<>\\]+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // webcal:// vira https://; o resto que não é http(s) é recusado (link colado errado).
    public static string? NormalizeUrl(string raw)
    {
        var url = raw.Trim();
        if (url.StartsWith("webcal://", StringComparison.OrdinalIgnoreCase)) url = "https://" + url[9..];
        return Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp) ? url : null;
    }

    public static string? JoinLink(params string?[] texts) =>
        texts.Select(t => t is null ? null : Join.Match(t)).FirstOrDefault(m => m is { Success: true })?.Value;

    // Puro: ocorrências que começam em [from, to) (horário local). .ics inválido = lista vazia.
    public static List<AgendaEvent> Parse(string ics, DateTime from, DateTime to)
    {
        try
        {
            var cal = Calendar.Load(ics);
            if (cal is null) return new();
            var toUtc = to.ToUniversalTime();
            return cal.GetOccurrences<CalendarEvent>(new CalDateTime(from.ToUniversalTime()))
                .TakeWhile(o => o.Period.StartTime.AsUtc < toUtc) // ocorrências vêm em ordem; RRULE sem fim é infinita
                .Select(o =>
                {
                    var e = (CalendarEvent)o.Source;
                    var start = o.Period.StartTime;
                    bool allDay = !start.HasTime;
                    // Dia todo é data "flutuante": não converte fuso, senão 11/11 vira 10/11 21:00.
                    var s = allDay ? start.Value.Date : start.AsUtc.ToLocalTime();
                    var end = o.Period.EffectiveEndTime is { } en ? (allDay ? en.Value.Date : en.AsUtc.ToLocalTime()) : s;
                    return new AgendaEvent(string.IsNullOrWhiteSpace(e.Summary) ? "(sem título)" : e.Summary.Trim(), s, end, allDay,
                        JoinLink(e.Location, e.Url?.ToString(), e.Description));
                })
                .ToList();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Agenda: .ics ilegível");
            return new();
        }
    }

    // Próximo aviso: o evento com hora que começa antes, ainda não avisado; o aviso sai `before` antes do início
    // (ou já, se o Orbit abriu em cima da hora). null = nada pela frente.
    public static (AgendaEvent Event, DateTime At)? NextReminder(IEnumerable<AgendaEvent> events, ISet<string> alerted, DateTime now, TimeSpan before)
    {
        var next = events.Where(e => !e.AllDay && e.Start > now && !alerted.Contains(e.Key)).MinBy(e => e.Start);
        if (next is null) return null;
        var at = next.Start - before;
        return (next, at < now ? now : at);
    }

    // Lista da aba Hoje: o que ainda não acabou hoje (dia todo primeiro, por ser o que vem antes na ordem).
    public static List<AgendaEvent> Remaining(IEnumerable<AgendaEvent> events, DateTime now, int max) =>
        events.Where(e => e.Start.Date == now.Date && (e.AllDay || e.End > now)).OrderBy(e => e.Start).Take(max).ToList();
}
