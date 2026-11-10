using System;
using System.Globalization;
using System.IO;
using Lattice.BigBase;

if (args.Length != 5 || !int.TryParse(args[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var seed))
{
    Console.Error.WriteLine("usage: Lattice.BigBase <catalogue.lcat> <vanilla-catalogue.lcat> <mix.txt> <seed> <out.tsv>");
    return 2;
}

var made = BigBaseTool.Make(args[0], args[1], args[2], seed);
if (!made.TryGet(out var result))
{
    Console.Error.WriteLine(made.Match(static _ => "", static why => why));
    return 1;
}

File.WriteAllText(args[4], result.Text);
Console.Write(result.Report);
return 0;
