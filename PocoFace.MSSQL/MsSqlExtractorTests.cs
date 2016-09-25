using NUnit.Framework;
using System.Collections.Generic;
using System.IO;

namespace PocoFace.MSSQL
{
    [TestFixture]
    public class MsSqlExtractorTests
    {
        [Test]
        public void test()
        {         
            var extractor = new MsSqlExtractor(@"EU101195-OSS\HR_STAG", "HRNET");
            extractor.ExcludedSchemas = new List<string>() { "Aura", "AuraUAT", "dbo" };
            var extracted = extractor.Extract();

            //var employee = extractor.Extract("HumanResources", "Employee");
            //var all = String.Join(Environment.NewLine, extracted);

            foreach (var @class in extracted)
            {
                using (var writer = new StreamWriter(@"C:\tmp\Pocos\" + @class.Key + ".cs"))
                {
                    writer.Write(@class.Value);
                }
            }

        }
    }
}
