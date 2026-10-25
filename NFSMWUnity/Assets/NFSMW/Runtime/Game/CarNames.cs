using System.Collections.Generic;

namespace NfsMw.Game
{
    /// <summary>Display names for the pvehicle / CARS folder names.</summary>
    public static class CarNames
    {
        static readonly Dictionary<string, string> Names = new Dictionary<string, string>
        {
            ["911gt2"] = "Porsche 911 GT2", ["911turbo"] = "Porsche 911 Turbo S", ["997s"] = "Porsche 911 Carrera S",
            ["a3"] = "Audi A3 3.2 quattro", ["a4"] = "Audi A4 3.2 FSI quattro", ["tt"] = "Audi TT 3.2 quattro",
            ["bmwm3gtr"] = "BMW M3 GTR", ["bmwm3gtre46"] = "BMW M3 GTR (Race)", ["camaro"] = "Chevrolet Camaro SS",
            ["carreragt"] = "Porsche Carrera GT", ["caymans"] = "Porsche Cayman S", ["clio"] = "Renault Clio V6",
            ["clk500"] = "Mercedes-Benz CLK 500", ["cobaltss"] = "Chevrolet Cobalt SS", ["corvette"] = "Chevrolet Corvette C6",
            ["corvettec6r"] = "Chevrolet Corvette C6.R", ["cts"] = "Cadillac CTS", ["db9"] = "Aston Martin DB9",
            ["eclipsegt"] = "Mitsubishi Eclipse GT", ["elise"] = "Lotus Elise", ["fordgt"] = "Ford GT", ["gallardo"] = "Lamborghini Gallardo",
            ["gti"] = "Volkswagen Golf GTI", ["gto"] = "Pontiac GTO", ["imprezawrx"] = "Subaru Impreza WRX STi", ["is300"] = "Lexus IS300",
            ["lancerevo8"] = "Mitsubishi Lancer Evolution VIII", ["monaro"] = "Vauxhall Monaro VXR", ["murcielago"] = "Lamborghini Murcielago",
            ["mustanggt"] = "Ford Mustang GT", ["punto"] = "Fiat Punto", ["rx7"] = "Mazda RX-7", ["rx8"] = "Mazda RX-8",
            ["sl500"] = "Mercedes-Benz SL 500", ["sl65"] = "Mercedes-Benz SL 65 AMG", ["slr"] = "Mercedes-Benz SLR McLaren",
            ["supra"] = "Toyota Supra", ["viper"] = "Dodge Viper SRT-10",
            ["copmidsize"] = "Cop", ["copsport"] = "Cross Corvette", ["copsuv"] = "Cop SUV", ["copgto"] = "Undercover Cop",
        };

        public static string Display(string car) => car != null && Names.TryGetValue(car.ToLowerInvariant(), out var n) ? n : car?.ToUpperInvariant();
    }
}
