using System.Collections.Generic;
using System.Linq;

namespace Orbit.Tabs.Mercado;

public enum MarketKind { Crypto, Currency }

// Uma opção disponível no catálogo do Mercado.
// Id = id do CoinGecko (cripto) ou código ISO da moeda (câmbio).
public record MarketCatalogItem(string Id, string Name, string Glyph, string ColorHex, MarketKind Kind);

// Lista curada de tudo que dá pra mostrar no tile. Glifo "●" (bullet colorido)
// pra criptos sem símbolo unicode próprio — a cor distingue.
public static class MarketCatalog
{
    public static readonly IReadOnlyList<MarketCatalogItem> Cryptos = new[]
    {
        new MarketCatalogItem("bitcoin",          "Bitcoin",           "₿", "#F7931A", MarketKind.Crypto),
        new MarketCatalogItem("ethereum",         "Ethereum",          "Ξ", "#8A92B2", MarketKind.Crypto),
        new MarketCatalogItem("solana",           "Solana",            "◎", "#14F195", MarketKind.Crypto),
        new MarketCatalogItem("binancecoin",      "BNB",               "◆", "#F3BA2F", MarketKind.Crypto),
        new MarketCatalogItem("ripple",           "XRP",               "●", "#23A0E8", MarketKind.Crypto),
        new MarketCatalogItem("cardano",          "Cardano",           "●", "#0033AD", MarketKind.Crypto),
        new MarketCatalogItem("dogecoin",         "Dogecoin",          "Ð", "#C2A633", MarketKind.Crypto),
        new MarketCatalogItem("avalanche-2",      "Avalanche",         "▲", "#E84142", MarketKind.Crypto),
        new MarketCatalogItem("polkadot",         "Polkadot",          "●", "#E6007A", MarketKind.Crypto),
        new MarketCatalogItem("chainlink",        "Chainlink",         "⬡", "#2A5ADA", MarketKind.Crypto),
        new MarketCatalogItem("matic-network",    "Polygon",           "⬟", "#8247E5", MarketKind.Crypto),
        new MarketCatalogItem("litecoin",         "Litecoin",          "Ł", "#A6A9AA", MarketKind.Crypto),
        new MarketCatalogItem("tron",             "Tron",              "▽", "#EF0027", MarketKind.Crypto),
        new MarketCatalogItem("the-open-network", "Toncoin",           "◈", "#0098EA", MarketKind.Crypto),
    };

    public static readonly IReadOnlyList<MarketCatalogItem> Currencies = new[]
    {
        new MarketCatalogItem("USD", "Dólar",             "$", "#5FCF80", MarketKind.Currency),
        new MarketCatalogItem("EUR", "Euro",              "€", "#6AA8FF", MarketKind.Currency),
        new MarketCatalogItem("GBP", "Libra",             "£", "#C9A227", MarketKind.Currency),
        new MarketCatalogItem("JPY", "Iene",              "¥", "#BC002D", MarketKind.Currency),
        new MarketCatalogItem("CHF", "Franco suíço",      "₣", "#D52B1E", MarketKind.Currency),
        new MarketCatalogItem("CAD", "Dólar canadense",   "$", "#D98C5F", MarketKind.Currency),
        new MarketCatalogItem("AUD", "Dólar australiano", "$", "#00843D", MarketKind.Currency),
        new MarketCatalogItem("CNY", "Yuan",              "¥", "#DE2910", MarketKind.Currency),
        new MarketCatalogItem("ARS", "Peso argentino",    "$", "#74ACDF", MarketKind.Currency),
    };

    public static MarketCatalogItem? FindCrypto(string id) => Cryptos.FirstOrDefault(c => c.Id == id);
    public static MarketCatalogItem? FindCurrency(string code) => Currencies.FirstOrDefault(c => c.Id == code);
}
