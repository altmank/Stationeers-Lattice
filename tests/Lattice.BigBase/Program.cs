using System;
using System.Globalization;
using System.IO;
using Lattice.TestKit.BigBase;

if (args.Length != 4 || !int.TryParse(args[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var seed))
{
    Console.Error.WriteLine("usage: Lattice.BigBase <catalogue.lcat> <mix.txt> <seed> <out.tsv>");
    return 2;
}

var made = BigBaseTool.Make(args[0], args[1], seed);
if (!made.TryGet(out var result))
{
    Console.Error.WriteLine(made.Match(static _ => "", static why => why));
    return 1;
}

File.WriteAllText(args[3], result.Text);
Console.Write(result.Report);
return 0;
