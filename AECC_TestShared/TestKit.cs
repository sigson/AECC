using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using AECC.Core;
using AECC.Core.Logging;
using AECC.Extensions;

namespace AECC.TestKit
{
    /// <summary>
    /// Общие константы протокола теста. ВАЖНО: instanceId мира ОДИНАКОВ на сервере и клиенте —
    /// иначе IDObject.ECSWorldOwnerId (сериализуемое поле!) и IECSObjectPathContainer.ECSWorldOwnerId
    /// после десериализации не резолвятся в локальный мир (ECSWorld.GetWorld вернёт fallback).
    /// </summary>
    public static class TK
    {
        public const long WorldId = 0x0A0E0C0C00000001L;

        public const string Host = "127.0.0.1";
        public const int Port = 6677;

        public const string User = "testuser1";
        public const string Password = "testpass1";
        public const string Email = "test@aecc.local";

        /// <summary>Период авторитарного роллинга сервера (мс).</summary>
        public const int RollIntervalMs = 60;

        // Имена сценарных нотисов server → client
        public const string N_ServerReady = "SERVER_READY";
        public const string N_WorldSpawned = "WORLD_SPAWNED";
        public const string N_ScoreRemoved = "SCORE_REMOVED";
        public const string N_ChestSent = "CHEST_SENT_WITHOUT_OWNER";
        public const string N_ChestOwnerSent = "CHEST_OWNER_SENT";
        public const string N_MoveApplied = "MOVE_APPLIED";
        public const string N_Summary = "SERVER_SUMMARY";

        // Команды client → server
        public const string C_Hello = "HELLO";
        public const string C_Move = "MOVE";
        public const string C_RemoveScore = "REMOVE_SCORE";
        public const string C_SendChest = "SEND_CHEST";
        public const string C_SendChestOwner = "SEND_CHEST_OWNER";
        public const string C_Damage = "DAMAGE_NPC";
        public const string C_Finish = "FINISH";

        public static long Uid<T>() { return typeof(T).TypeId(); }
    }

    public sealed class TestResult
    {
        public string Section;
        public string Name;
        public bool Ok;
        public string Detail;
    }

    /// <summary>
    /// Минималистичный потокобезопасный ранер: Check/Section/Await + печать таблицы + exit-code.
    /// Специально без внешних зависимостей (никакого xunit) — тест должен запускаться как обычный exe.
    /// </summary>
    public sealed class TestReport
    {
        private readonly ConcurrentQueue<TestResult> _results = new ConcurrentQueue<TestResult>();
        private string _section = "general";
        private readonly string _title;
        private readonly Stopwatch _sw = Stopwatch.StartNew();

        public TestReport(string title) { _title = title; }

        public void Section(string name)
        {
            _section = name;
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine();
            Console.WriteLine("──── " + name + " " + new string('─', Math.Max(0, 60 - name.Length)));
            Console.ResetColor();
        }

        public bool Check(string name, bool ok, string detail = "")
        {
            _results.Enqueue(new TestResult { Section = _section, Name = name, Ok = ok, Detail = detail });
            Console.ForegroundColor = ok ? ConsoleColor.Green : ConsoleColor.Red;
            Console.WriteLine((ok ? "  [ OK ] " : "  [FAIL] ") + name + (string.IsNullOrEmpty(detail) ? "" : "   → " + detail));
            Console.ResetColor();
            return ok;
        }

        public bool CheckEq<T>(string name, T expected, T actual)
        {
            bool ok = EqualityComparer<T>.Default.Equals(expected, actual);
            return Check(name, ok, ok ? "" : string.Format("ожидалось <{0}>, получено <{1}>", expected, actual));
        }

        /// <summary>Оборачивает блок: исключение = провал теста, а не падение процесса.</summary>
        public void Try(string name, Action body)
        {
            try
            {
                body();
                Check(name, true);
            }
            catch (Exception ex)
            {
                Check(name, false, ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>Активное ожидание условия (движок асинхронный: реакции идут через пул).</summary>
        public static bool Await(Func<bool> condition, int timeoutMs = 3000, int pollMs = 10)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                try { if (condition()) return true; }
                catch { /* объект ещё не приехал */ }
                Thread.Sleep(pollMs);
            }
            try { return condition(); } catch { return false; }
        }

        public bool AwaitCheck(string name, Func<bool> condition, int timeoutMs = 3000)
        {
            bool ok = Await(condition, timeoutMs);
            return Check(name, ok, ok ? "" : "таймаут " + timeoutMs + " мс");
        }

        public int Total { get { return _results.Count; } }
        public int Failed { get { return _results.Count(r => !r.Ok); } }
        public int Passed { get { return _results.Count(r => r.Ok); } }

        public IEnumerable<TestResult> Results { get { return _results.ToArray(); } }

        public void PrintSummary()
        {
            Console.WriteLine();
            Console.WriteLine("═════════════════════════════════════════════════════════════");
            Console.WriteLine("  " + _title);
            Console.WriteLine("═════════════════════════════════════════════════════════════");
            foreach (var g in _results.GroupBy(r => r.Section))
            {
                int f = g.Count(r => !r.Ok);
                Console.ForegroundColor = f == 0 ? ConsoleColor.Green : ConsoleColor.Red;
                Console.WriteLine(string.Format("  {0,-42} {1,3}/{2,-3} {3}",
                    g.Key, g.Count(r => r.Ok), g.Count(), f == 0 ? "OK" : "FAILED"));
                Console.ResetColor();
                foreach (var r in g.Where(x => !x.Ok))
                    Console.WriteLine("        ✗ " + r.Name + (string.IsNullOrEmpty(r.Detail) ? "" : "  (" + r.Detail + ")"));
            }
            Console.WriteLine("─────────────────────────────────────────────────────────────");
            Console.ForegroundColor = Failed == 0 ? ConsoleColor.Green : ConsoleColor.Red;
            Console.WriteLine(string.Format("  ИТОГО: {0} проверок, провалено {1}, время {2:0.0} c",
                Total, Failed, _sw.Elapsed.TotalSeconds));
            Console.ResetColor();
            Console.WriteLine("═════════════════════════════════════════════════════════════");
        }

        public string ToCompactString()
        {
            return string.Format("{0}: {1}/{2} passed, {3} failed", _title, Passed, Total, Failed);
        }
    }

    public static class Bootstrapping
    {
        /// <summary>
        /// Обязательные глобальные флаги ДО создания любых миров/хранилищ:
        /// режим конкуренции фиксируется в момент конструирования ComponentBag/RWLock.
        /// </summary>
        public static void ConfigureKernel(bool multiThread = true)
        {
            Defines.ThreadsMode = true;
            Defines.OneThreadMode = !multiThread;       // false ⇒ ConcurrencyMode.MultiThread
            Defines.IgnoreNonDangerousExceptions = true;
            Defines.TrackRemovedComponents = true;
            Defines.SerializatorTypesLog = false;
            ECSExecutableContractContainer.CaptureGenerationStackTrace = true;
        }

        /// <summary>Мир + сериализация + query-индекс. instanceId выставляется ДО Configure.</summary>
        public static ECSWorld CreateWorld(long instanceId, ECSWorld.WorldTypeEnum type,
                                           AECC.Serialization.ISerializationAdapter adapter,
                                           Func<Type, bool> contractFilter = null)
        {
            var world = new ECSWorld();
            world.instanceId = instanceId;
            world.WorldType = type;
            world.WorldMetaData = type.ToString();
            world.Configure(staticContractFiltering: contractFilter);
            AECC.Runtime.Bootstrap.AttachRuntime(world, adapter); // SerializationBootstrap + QueryBootstrap
            world.Start();
            return world;
        }

        /// <summary>
        /// ECSService.InitializeProcess() перекрывает ECSWorld.GetWorld на create-on-miss своей WorldDB.
        /// Возвращаем канон: резолв из WorldRegistry (туда мир попадает в Configure).
        /// Вызывать ПОСЛЕ IService.InitializeAllServices().
        /// </summary>
        public static void RestoreWorldResolver()
        {
            ECSWorld.GetWorld = (id) =>
            {
                ECSWorld w;
                if (WorldRegistry.Default.TryGet(id, out w)) return w;
                NLogger.Error("[TestKit] мир " + id + " не найден в WorldRegistry");
                return null;
            };
        }
    }
}
