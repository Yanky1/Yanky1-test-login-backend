namespace Solvelt.Api;
/// <summary>THROWAWAY mutation switch (branch mutation/quantity-offers-c18-guards only, never merged): SOLVELT_MUT=Mn reverts one guarded line.</summary>
public static class Mut { public static bool On(string id) => Environment.GetEnvironmentVariable("SOLVELT_MUT") == id; }
