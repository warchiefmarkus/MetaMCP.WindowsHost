using MetaMCP.Host;

var failures = 0;
var passed = 0;

void Test(string name, Action action)
{
    try
    {
        action();
        passed++;
        Console.WriteLine("PASS " + name);
    }
    catch (Exception ex)
    {
        failures++;
        Console.Error.WriteLine("FAIL " + name + ": " + ex.Message);
    }
}

Dictionary<string, string> Parse(params string[] lines)
{
    var path = Path.Combine(Path.GetTempPath(), "metamcp-env-" + Guid.NewGuid() + ".local");
    try
    {
        File.WriteAllLines(path, lines);
        return EnvFile.Load(path);
    }
    finally
    {
        File.Delete(path);
    }
}

void Equal(string expected, string actual)
{
    if (!string.Equals(expected, actual, StringComparison.Ordinal))
    {
        throw new Exception("Expected '" + expected + "', got '" + actual + "'");
    }
}

void Throws<TException>(Action action) where TException : Exception
{
    try
    {
        action();
    }
    catch (TException)
    {
        return;
    }

    throw new Exception("Expected " + typeof(TException).Name);
}

Test("PostgreSQL connection string with forward references", () =>
{
    var vars = Parse(
        "DATABASE_URL=postgresql://${POSTGRES_USER}:${POSTGRES_PASSWORD}@${POSTGRES_HOST}:${POSTGRES_PORT}/${POSTGRES_DB}",
        "POSTGRES_HOST=127.0.0.1",
        "POSTGRES_USER=metamcp_user",
        "POSTGRES_PASSWORD=example-secret",
        "POSTGRES_PORT=5432",
        "POSTGRES_DB=metamcp_db");
    Equal("postgresql://metamcp_user:example-secret@127.0.0.1:5432/metamcp_db", vars["DATABASE_URL"]);
});

Test("Nested references resolve recursively", () =>
{
    var vars = Parse("FIRST=${SECOND}", "SECOND=${THIRD}", "THIRD=ready");
    Equal("ready", vars["FIRST"]);
});

Test("Quoted values and trailing comments", () =>
{
    var vars = Parse(
        "LOG_LEVEL='errors-only' #'all', 'info', 'none'",
        "BOOTSTRAP_ENABLE=true # enable initial setup",
        "BOOTSTRAP_USER_NAME=\"Administrator #1\" # this is a comment");
    Equal("errors-only", vars["LOG_LEVEL"]);
    Equal("true", vars["BOOTSTRAP_ENABLE"]);
    Equal("Administrator #1", vars["BOOTSTRAP_USER_NAME"]);
});

Test("Hashes within unquoted values remain intact", () =>
{
    var vars = Parse("PASSWORD=pass#part", "URL=http://localhost/path#fragment");
    Equal("pass#part", vars["PASSWORD"]);
    Equal("http://localhost/path#fragment", vars["URL"]);
});

Test("Export prefix and empty values", () =>
{
    var vars = Parse("export HOST=127.0.0.1", "EMPTY=", "# ignored");
    Equal("127.0.0.1", vars["HOST"]);
    Equal("", vars["EMPTY"]);
});

Test("File values override process environment", () =>
{
    const string name = "METAMCP_ENV_FILE_TEST";
    var previous = Environment.GetEnvironmentVariable(name);
    try
    {
        Environment.SetEnvironmentVariable(name, "external");
        var vars = Parse("METAMCP_ENV_FILE_TEST=internal", "LOOKUP=${METAMCP_ENV_FILE_TEST}");
        Equal("internal", vars["LOOKUP"]);
    }
    finally
    {
        Environment.SetEnvironmentVariable(name, previous);
    }
});

Test("Process environment fallback", () =>
{
    const string name = "METAMCP_ENV_FALLBACK_TEST";
    var previous = Environment.GetEnvironmentVariable(name);
    try
    {
        Environment.SetEnvironmentVariable(name, "from-process");
        var vars = Parse("LOOKUP=${METAMCP_ENV_FALLBACK_TEST}");
        Equal("from-process", vars["LOOKUP"]);
    }
    finally
    {
        Environment.SetEnvironmentVariable(name, previous);
    }
});

Test("Unknown variables produce an error", () =>
{
    Throws<InvalidDataException>(() => Parse("LOOKUP=${METAMCP_UNDEFINED_ENV_VAR_12345}"));
});

Test("Circular references produce an error", () =>
{
    Throws<InvalidDataException>(() => Parse("A=${B}", "B=${A}"));
});

Test("Self references produce an error", () =>
{
    Throws<InvalidDataException>(() => Parse("A=${A}"));
});

Test("Missing optional file returns an empty set", () =>
{
    var values = EnvFile.Load(Path.Combine(Path.GetTempPath(), "missing-" + Guid.NewGuid()));
    if (values.Count != 0) throw new Exception("Expected an empty dictionary");
});

Console.WriteLine("Environment loader tests: " + passed + " passed; " + failures + " failed.");
return failures == 0 ? 0 : 1;
