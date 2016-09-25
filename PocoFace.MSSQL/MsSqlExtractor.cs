using Microsoft.SqlServer.Management.Common;
using Microsoft.SqlServer.Management.Smo;
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Entity.Design.PluralizationServices;
using System.Data.SqlClient;
using System.Globalization;
using System.Linq;
using System.Text;

namespace PocoFace.MSSQL
{
    public class MsSqlExtractor
    {
        public List<string> IncludedSchemas { get; set; }
        public List<string> ExcludedSchemas { get; set; }

        public bool IncludeNavigationLinks { get; set; }
        public bool IncludeConstructor { get; set; }
        public bool SingleFile { get; set; }
        public string BaseClass { get; set; }
        public List<string> Interfaces { get; set; }
        public string CustomCode { get; set; }

        public string Host { get; private set; }
        public string UserId { get; set; }
        public string Password { get; set; }
        public string Namespace { get; set; }

        private string dataBase;
        public string Database
        {
            get { return dataBase; }
            set
            {
                if (value != dataBase)
                {
                    dataBase = value;
                    CollectTables();
                }
            }
        }

        private List<Schema> schemas = new List<Schema>();
        private List<Table> tables = new List<Table>();
        private void CollectTables()
        {
            SqlConnection connection = new SqlConnection(BuildSqlConnectionString());
            ServerConnection serverConnection = new ServerConnection(connection);
            Server server = new Server(serverConnection);
            Database database = server.Databases[Database];

            schemas = new List<Schema>(database.Schemas.Cast<Schema>());
            tables = new List<Table>(database.Tables.Cast<Table>());
        }

        public MsSqlExtractor(string host, string database, string userId = "", string password = "")
        {
            IncludedSchemas = new List<string>();
            ExcludedSchemas = new List<string>();
            Interfaces = new List<string>();

            IncludeNavigationLinks = true;
            IncludeConstructor = true;
            SingleFile = false;

            Host = host;
            Database = database;

            var connectionOk = Validate();

            if (!connectionOk.Item1)
                throw connectionOk.Item2;

            UserId = userId;
            Password = password;
        }

        public Dictionary<string, string> Extract()
        {
            if (tables == null)
                CollectTables();

            var results = new Dictionary<string, string>();

            foreach (var table in tables)
            {
                var result = Extract(table.Schema, table.Name);

                if (!String.IsNullOrEmpty(result.Key))
                    results.Add(result.Key, result.Value);
            }

            return results;
        }

        public string Extract(string schema)
        {
            throw new NotImplementedException();
        }

        public KeyValuePair<string, string> Extract(string schema, string table)
        {
            if (ExcludedSchemas.Contains(schema))
                return new KeyValuePair<string, string>();

            var properties = CreateProperties(schema, table);
            var navigationProperties = IncludeNavigationLinks
                                ? CreateNavigationProperties(schema, table)
                                : new List<string>();

            var constructor = IncludeConstructor
                ? CreateConstructor(schema, table)
                : null;

            var classHeader = CreateClassHeader(table);

            var poco = new StringBuilder();

            poco.AppendLine("using System;");
            poco.AppendLine("using System.Collections.Generic;");
            poco.AppendLine();

            if (string.IsNullOrEmpty(Namespace))
                poco.AppendLine($"namespace {Database}.{schema}");
            else
                poco.AppendLine($"namespace {Namespace}");

            poco.AppendLine("{");
            poco.AppendLine(classHeader);
            poco.AppendLine("\t{");
            
            if (IncludeConstructor && !String.IsNullOrEmpty(constructor))
                poco.AppendLine(constructor);

            properties.ForEach(p => poco.AppendLine(p));
            
            if (navigationProperties.Count > 0)
            {
                poco.AppendLine();
                navigationProperties.ForEach(p => poco.AppendLine(p));
            }
            
            poco.AppendLine("\t}");
            poco.AppendLine("}");

            return new KeyValuePair<string, string>($"{schema}.{table}", poco.ToString());
        }

        private string CreateClassHeader(string name)
        {
            //if(!String.IsNullOrEmpty(BaseClass))
            return $"\tpublic class {name}";
        }

        public List<string> CreateProperties(string schemaName, string tableName = "")
        {
            var result = new List<string>();

            using (var connection = new SqlConnection(BuildSqlConnectionString()))
            {
                connection.Open();

                var query = $"select top 0 * from [{schemaName}].[{tableName}]";
                var command = new SqlCommand(query, connection);
                var schema = command.ExecuteReader(CommandBehavior.SchemaOnly).GetSchemaTable();

                foreach (DataRow row in schema.Rows)
                {
                    var name = (string)row["ColumnName"];
                    var type = (string)row["DataTypeName"];
                    var length = (int)row["ColumnSize"];
                    var readOnly = (bool)row["IsReadOnly"];
                    var identity = (bool)row["IsIdentity"];
                    var clrType = ConvertType(type);
                    var comment = String.Empty;

                    if (clrType == null)
                    {
                        comment = "//";
                        clrType = type;
                    }

                    if (!identity && readOnly)
                        continue;

                    result.Add($"\t\t{comment}public {clrType} {name} {{ get; set; }}");
                };

                connection.Close();
            }

            return result;
        }

        public string CreateConstructor(string schemaName, string tableName)
        {
            Table table = tables
                .Where(t => t.Schema == schemaName
                            && t.Name == tableName).SingleOrDefault();

            if (table == null || !table.ForeignKeys.Cast<ForeignKey>().Any())
                return null;

            var builder = new StringBuilder();

            builder.AppendLine($"\t\tpublic {tableName}()");
            builder.AppendLine("\t\t{");

            foreach (ForeignKey foreignKey in table.ForeignKeys)
            {
                var name = foreignKey.ReferencedTable;
                var plural = Pluralize(name);

                builder.AppendLine($"\t\t\t{plural} = new List<{name}>();");
            }

            builder.AppendLine("\t\t}");

            return builder.ToString();
        }

        public List<string> CreateNavigationProperties(string schemaName, string tableName)
        {
            Table table = tables
                .Where(t => t.Schema == schemaName
                            && t.Name == tableName).SingleOrDefault();

            if (table == null || !table.ForeignKeys.Cast<ForeignKey>().Any())
                return new List<string>();

            var result = new List<string>();

            foreach (ForeignKey foreignKey in table.ForeignKeys)
            {
                var name = foreignKey.ReferencedTable;
                var plural = Pluralize(name);

                result.Add($"\t\tpublic List<{name}> {plural} {{ get; set; }}");
            }

            return result;
        }

        public static string ConvertType(string type)
        {
            type = type.ToLower();

            var types = new Dictionary<string, string>()
            {
                //{"bigint","Int64"},
                {"bigint","long"},
                {"binary","byte[]"},
                {"bit","bool"},
                {"char","string"},
                {"date","DateTime"},
                {"datetime","DateTime"},
                {"datetime2","DateTime"},
                {"datetimeoffset","DateTimeOffset"},
                {"decimal","decimal"},
                {"float","Double"},
                {"image","byte[]"},
                //{"int","Int32"},
                {"int","int"},
                {"money","decimal"},
                {"nchar","string"},
                {"ntext","string"},
                {"numeric","decimal"},
                {"nvarchar","string"},
                {"real","Single"},
                {"rowversion","byte[]"},
                {"smalldatetime","DateTime"},
                {"smallint","Int16"},
                {"smallmoney","decimal"},
                {"text","string"},
                {"time","TimeSpan"},
                {"timestamp","byte[]"},
                {"tinyint","byte"},
                {"uniqueidentifier","Guid"},
                {"varbinary","byte[]"},
                {"varchar","string"},
                {"xml","Xml"}
            };

            if (types.ContainsKey(type))
                return types[type];
            else
                return null;
        }

        public static string Pluralize(string singular, CultureInfo cultureInfo = null)
        {
            if (cultureInfo == null)
                cultureInfo = CultureInfo.GetCultureInfo("en-US");
            else
            {
                if (cultureInfo != CultureInfo.GetCultureInfo("en-US"))
                    throw new NotImplementedException("Currently only en-US pluralization is supported.");
            }

            PluralizationService service = PluralizationService.CreateService(cultureInfo);

            if (service.IsPlural(singular))
                return singular;

            return service.Pluralize(singular);
        }

        public Tuple<bool, Exception> Validate()
        {
            using (var connection = new SqlConnection(BuildSqlConnectionString()))
            {
                try
                {
                    connection.Open();
                }
                catch (Exception ex)
                {
                    return new Tuple<bool, Exception>(false, ex);
                }
            }

            return new Tuple<bool, Exception>(true, null);
        }

        private string BuildSqlConnectionString()
        {
            var builder = new SqlConnectionStringBuilder();

            builder.DataSource = Host;
            if (string.IsNullOrEmpty(UserId))
                builder.IntegratedSecurity = true;
            else
            {
                builder.UserID = UserId;
                builder.Password = Password;
            }
            builder.InitialCatalog = Database;
            builder.ConnectTimeout = 500;

            return builder.ConnectionString;
        }
    }
}
