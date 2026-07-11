using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.IO;
using System.Linq;
using AECC.Core.Logging;
using AECC.Harness.Model;
using AECC.Harness.Services;
using Microsoft.Data.Sqlite;

namespace AECC.TestServer
{
    /// <summary>
    /// Провайдер БД для теста.
    ///
    /// ПОЧЕМУ НЕ SQLiteDefaultDBProvider ИЗ ФРЕЙМВОРКА:
    /// он целиком обёрнут в `#if NET && !GODOT`, а AECC_Framework таргетит netstandard2.0,
    /// где символ NET не определён ⇒ класс просто не компилируется, и
    /// DBService.InitializeProcess() оставляет DBProvider == null (см. FRAMEWORK_MAP §10.3).
    /// По той же причине не работает UserDataRowBase.DBUnpack — распаковку строки делаем сами.
    ///
    /// Отличия от оригинала (осознанно): все запросы ПАРАМЕТРИЗОВАНЫ (в оригинале —
    /// конкатенация строк, т.е. SQL-инъекция, см. FRAMEWORK_MAP §10.4).
    /// </summary>
    public sealed class SqliteDbProvider : IDBProvider
    {
        private SqliteConnection _connection;
        private readonly object _gate = new object();

        public string ResolvedPath { get; private set; }

        private SqliteConnection Conn
        {
            get
            {
                if (_connection != null) return _connection;
                lock (_gate)
                {
                    if (_connection != null) return _connection;

                    // DBService.DBPath заполняется из baseconfig (DataBase/DBPath) на его шаге инициализации.
                    var rel = DBService.instance != null && !string.IsNullOrEmpty(DBService.instance.DBPath)
                        ? DBService.instance.DBPath
                        : Path.Combine("Config", "Users.db");

                    var full = Path.Combine(GlobalProgramState.instance.GameDataDir, rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(full));
                    ResolvedPath = full;

                    var cs = "Data Source=" + full + ";Cache=Shared;Mode=ReadWriteCreate;";
                    NLogger.LogDB("[TestDB] " + cs);

                    var c = new SqliteConnection(cs);
                    c.Open();
                    _connection = c;
                    SetupSchema();
                    return _connection;
                }
            }
        }

        public void EnsureOpen() { var _ = Conn; }

        private void SetupSchema()
        {
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = SchemaSql;
                cmd.ExecuteNonQuery();
            }
        }

        private const string SchemaSql = @"
CREATE TABLE IF NOT EXISTS ""Users"" (
    ""id""                  INTEGER NOT NULL,
    ""Username""            VARCHAR(20) NOT NULL COLLATE NOCASE,
    ""Password""            VARCHAR(44) NOT NULL,
    ""Email""               TEXT,
    ""EmailVerified""       TINYINT(1) NOT NULL DEFAULT 0,
    ""HardwareId""          TEXT NOT NULL DEFAULT '',
    ""RegistrationDate""    TEXT NOT NULL DEFAULT '',
    ""UserPrivilegesGroup"" TEXT NOT NULL DEFAULT 'user',
    ""LastIp""              TEXT NOT NULL DEFAULT '',
    ""TermlessChatBan""     TINYINT(1) NOT NULL DEFAULT 0,
    ""TermlessBan""         TINYINT(1) NOT NULL DEFAULT 0,
    ""UserLocation""        TEXT NOT NULL DEFAULT 'en',
    ""Karma""               INTEGER NOT NULL DEFAULT 0,
    ""GameDataPacked""      TEXT NOT NULL DEFAULT '',
    PRIMARY KEY(""id"" AUTOINCREMENT)
);";

        public override void Load(string DBPath)
        {
            EnsureOpen();
        }

        public override List<T> ExecuteQuery<T>(string query)
        {
            throw new NotImplementedException();
        }

        public override bool UsernameAvailable(string username)
        {
            using (var cmd = Conn.CreateCommand())
            {
                cmd.CommandText = "SELECT id FROM Users WHERE Username = @u COLLATE NOCASE;";
                cmd.Parameters.AddWithValue("@u", username ?? "");
                using (var rd = cmd.ExecuteReader(CommandBehavior.SingleRow))
                    return !rd.HasRows;
            }
        }

        public override bool EmailAvailable(string email)
        {
            using (var cmd = Conn.CreateCommand())
            {
                cmd.CommandText = "SELECT id FROM Users WHERE Email = @e;";
                cmd.Parameters.AddWithValue("@e", email ?? "");
                using (var rd = cmd.ExecuteReader(CommandBehavior.SingleRow))
                    return !rd.HasRows;
            }
        }

        public override bool LoginCheck(string username, string hashedPassword)
        {
            using (var cmd = Conn.CreateCommand())
            {
                cmd.CommandText = "SELECT id FROM Users WHERE Username = @u COLLATE NOCASE AND Password = @p;";
                cmd.Parameters.AddWithValue("@u", username ?? "");
                cmd.Parameters.AddWithValue("@p", hashedPassword ?? "");
                using (var rd = cmd.ExecuteReader(CommandBehavior.SingleRow))
                    return rd.HasRows;
            }
        }

        public override T CreateUser<T>(T dataRow)
        {
            if (!EmailAvailable(dataRow.Email)) throw new ArgumentException("Email Taken!");
            if (!UsernameAvailable(dataRow.Username)) throw new ArgumentException("Username Taken!");

            var packed = dataRow.PrepareToDBInsert();
            var columns = string.Join(", ", packed.Item1);
            var paramNames = string.Join(", ", packed.Item1.Select(c => "@" + c));

            using (var cmd = Conn.CreateCommand())
            {
                cmd.CommandText = "INSERT INTO Users(" + columns + ") VALUES(" + paramNames + ");";
                for (int i = 0; i < packed.Item1.Count; i++)
                    cmd.Parameters.AddWithValue("@" + packed.Item1[i], (object)packed.Item2[i] ?? DBNull.Value);
                cmd.ExecuteNonQuery();
            }
            return GetUserViaCallsign<T>(dataRow.Username);
        }

        public override T CreateOrUpdateUser<T>(T dataRow)
        {
            var existing = GetUserViaCallsign<T>(dataRow.Username);
            if (existing == null) return CreateUser(dataRow);

            var packed = dataRow.PrepareToDBInsert();
            var sets = packed.Item1
                .Where(c => !c.Equals("Username", StringComparison.OrdinalIgnoreCase))
                .Select(c => c + " = @" + c);

            using (var cmd = Conn.CreateCommand())
            {
                cmd.CommandText = "UPDATE Users SET " + string.Join(", ", sets) + " WHERE Username = @Username;";
                for (int i = 0; i < packed.Item1.Count; i++)
                    cmd.Parameters.AddWithValue("@" + packed.Item1[i], (object)packed.Item2[i] ?? DBNull.Value);
                cmd.ExecuteNonQuery();
            }
            return GetUserViaCallsign<T>(dataRow.Username);
        }

        public override T GetUserViaCallsign<T>(string username)
        {
            return QueryOne<T>("SELECT * FROM Users WHERE Username = @v COLLATE NOCASE;", username);
        }

        public override T GetUserViaEmail<T>(string email)
        {
            return QueryOne<T>("SELECT * FROM Users WHERE Email = @v;", email);
        }

        private T QueryOne<T>(string sql, string value) where T : UserDataRowBase
        {
            using (var cmd = Conn.CreateCommand())
            {
                cmd.CommandText = sql;
                cmd.Parameters.AddWithValue("@v", value ?? "");
                using (var rd = cmd.ExecuteReader(CommandBehavior.SingleRow))
                {
                    if (!rd.HasRows) return default(T);
                    rd.Read();
                    var row = Activator.CreateInstance<T>();
                    Unpack(row, rd);
                    return row;
                }
            }
        }

        /// <summary>UserDataRowBase.DBUnpack вырезан препроцессором в netstandard2.0 — распаковываем сами.</summary>
        private static void Unpack(UserDataRowBase row, DbDataReader rd)
        {
            row.Id = Convert.ToInt64(rd["id"]);
            row.Username = Str(rd["Username"]);
            row.Password = Str(rd["Password"]);
            row.Email = Str(rd["Email"]);
            row.EmailVerified = Bool(rd["EmailVerified"]);
            row.HardwareId = Str(rd["HardwareId"]);
            row.RegistrationDate = Str(rd["RegistrationDate"]);
            row.UserPrivilegesGroup = Str(rd["UserPrivilegesGroup"]);
            row.LastIp = Str(rd["LastIp"]);
            row.TermlessChatBan = Bool(rd["TermlessChatBan"]);
            row.TermlessBan = Bool(rd["TermlessBan"]);
            row.UserLocation = Str(rd["UserLocation"]);
            row.Karma = (int)Convert.ToInt64(rd["Karma"] is DBNull ? 0L : rd["Karma"]);
            row.GameDataPacked = Str(rd["GameDataPacked"]);
        }

        private static string Str(object o) { return o == null || o is DBNull ? "" : Convert.ToString(o); }

        private static bool Bool(object o)
        {
            if (o == null || o is DBNull) return false;
            var s = Convert.ToString(o);
            bool b;
            if (bool.TryParse(s, out b)) return b;
            long l;
            if (long.TryParse(s, out l)) return l != 0;
            return false;
        }

        public override List<string> GetEmailList()
        {
            var result = new List<string>();
            using (var cmd = Conn.CreateCommand())
            {
                cmd.CommandText = "SELECT Email FROM Users WHERE Email <> '' AND EmailVerified <> 0;";
                using (var rd = cmd.ExecuteReader())
                    while (rd.Read()) result.Add(Str(rd["Email"]));
            }
            return result;
        }

        private bool Exec(string sql, params (string, object)[] ps)
        {
            using (var cmd = Conn.CreateCommand())
            {
                cmd.CommandText = sql;
                foreach (var p in ps) cmd.Parameters.AddWithValue(p.Item1, p.Item2 ?? DBNull.Value);
                return cmd.ExecuteNonQuery() > 0;
            }
        }

        public override bool SetUsername(long uid, string newUsername)
        {
            return Exec("UPDATE Users SET Username = @n WHERE id = @id;", ("@n", newUsername), ("@id", uid));
        }

        public override bool SetHashedPassword(long uid, string hashedPassword)
        {
            if (hashedPassword != null && hashedPassword.Length > 44)
                throw new ArgumentException("Parameter 'hashedPassword' is too long!");
            return Exec("UPDATE Users SET Password = @p WHERE id = @id;", ("@p", hashedPassword), ("@id", uid));
        }

        public override bool SetEmail(long uid, string email)
        {
            return Exec("UPDATE Users SET Email = @e, EmailVerified = 0 WHERE id = @id;", ("@e", email), ("@id", uid));
        }

        public override bool SetEmailVerified(long uid, bool value)
        {
            return Exec("UPDATE Users SET EmailVerified = @v WHERE id = @id;", ("@v", value ? 1 : 0), ("@id", uid));
        }

        public override bool SetHardwareId(long uid, string hardwareId)
        {
            if (hardwareId != null && hardwareId.Length > 100)
                throw new ArgumentException("Parameter hardwareId cannot not be over 100 characters");
            return Exec("UPDATE Users SET HardwareId = @h WHERE id = @id;", ("@h", hardwareId), ("@id", uid));
        }

        /// <summary>Утилита теста: чистый старт.</summary>
        public void DropUser(string username)
        {
            Exec("DELETE FROM Users WHERE Username = @u COLLATE NOCASE;", ("@u", username));
        }

        public int CountUsers()
        {
            using (var cmd = Conn.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM Users;";
                return Convert.ToInt32(cmd.ExecuteScalar());
            }
        }
    }
}
