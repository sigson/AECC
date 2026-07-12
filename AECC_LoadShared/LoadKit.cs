using System;
using AECC.Extensions;

namespace AECC.LoadKit
{
    /// <summary>
    /// LK — константы нагрузочного сессионного теста.
    ///
    /// ВАЖНО (как и в базовом тест-ките): этот файл компилируется В ОБА проекта
    /// (AECC_LoadServer и AECC_LoadClient) из общей папки. NetSerializer вычисляет id типа
    /// на проводе как CRC32(Type.ToString()) с вырезанными namespace'ами корневых типов,
    /// поэтому набор IDObject-типов и их namespace обязаны совпадать буквально.
    ///
    /// Все "нагрузочные" параметры — static-поля (не const), чтобы их можно было
    /// переопределять переменными окружения AECC_LOAD_&lt;ИМЯ&gt; без пересборки
    /// (например AECC_LOAD_VERIFYMODE=false — чистая нагрузка без двойных проверок).
    /// Сервер и клиент читают ОДНИ И ТЕ ЖЕ переменные — держите окружение одинаковым.
    /// </summary>
    public static class LK
    {
        // ── Идентичность мира: ОДИНАКОВА на сервере и клиенте (см. TK.WorldId в TestKit.cs:
        //    иначе сериализуемые ECSWorldOwnerId / IECSObjectPathContainer.ECSWorldOwnerId
        //    после десериализации не резолвятся в локальный мир). ──
        public const long WorldId = 0x0A0E0C0C000000A2L;

        public const string Host = "127.0.0.1";
        public const int Port = 6688;
        public const string Password = "loadpass1";
        public const string EmailDomain = "@load.aecc.local";

        // ─────────────────────────────────────────────────────────────────────
        //  КОНСТАНТЫ, ЗАПРОШЕННЫЕ ПОСТАНОВКОЙ
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>Максимум пользователей на сервере (HELLO сверх лимита ⇒ SERVER_FULL).</summary>
        public static int MaxUsersOnServer = 24;

        /// <summary>Максимум пользователей в одной сессии.</summary>
        public static int MaxUsersPerSession = 4;

        /// <summary>Ёмкость операционных компонентов (задел): списки пушек создаются на 32 слота.</summary>
        public const int MaxOperationalComponents = 32;

        /// <summary>Сколько пушек реально активно у игрока (≤ MaxOperationalComponents).</summary>
        public static int GunsPerPlayer = 4;

        /// <summary>Частота сессионной операции: выстрелов в секунду НА ИГРОКА,
        /// распределяется round-robin по всем его пушкам.</summary>
        public static double ShotsPerSecond = 6.0;

        /// <summary>Максимум сессий на сервер (все генерируются на старте сервера).</summary>
        public static int MaxSessionsOnServer = 6;

        /// <summary>Килл-каунт сессии: случайный, но НЕ МЕНЕЕ 100.</summary>
        public static int SessionKillTargetMin = 100;
        public static int SessionKillTargetSpan = 40;   // цель = Min + rand(0..Span)

        /// <summary>Перезапуск сессии по достижении килл-каунта занимает 5 секунд.</summary>
        public static int SessionRestartMs = 5000;

        /// <summary>Прогрессия цены улучшения пушки: cost(level) = Base * 1.1^level, бесконечно.</summary>
        public static double UpgradeCostGrowth = 1.1;
        public static long UpgradeBaseCost = 10;

        /// <summary>Экономика: золото за килл + бонус лучшему киллеру раунда.</summary>
        public static long GoldPerKill = 5;
        public static long TopKillerBonus = 25;

        /// <summary>ХП у всех одно — константа. Другие игроки его видят, владелец — нет (GDAP).</summary>
        public static int PlayerMaxHp = 100;

        /// <summary>Урон пушки = Base + Level * PerLevel, умноженный на модификатор сессии.</summary>
        public static int GunBaseDamage = 15;
        public static int GunDamagePerLevel = 3;

        /// <summary>Откатываемая способность постановки мин: 7 раз в секунду.</summary>
        public static double MinePlacesPerSecond = 7.0;

        /// <summary>Мина случайно взрывается в интервале 1..2 секунды после постановки.</summary>
        public static int MineFuseMinMs = 1000;
        public static int MineFuseMaxMs = 2000;

        /// <summary>Урон мины — всем игрокам сессии разом.</summary>
        public static int MineDamage = 20;

        /// <summary>Модификаторы сессии («повышенный урон»): выбирается случайно на раунд.</summary>
        public static readonly double[] SessionDamageModifiers = { 1.0, 1.25, 1.5 };

        /// <summary>ДВОЙНЫЕ ПРОВЕРКИ клиент⇄сервер. false — «истинная нагрузочная способность»:
        /// отключает wire-инспекцию GDAP, StateCheck-циклы, ShootRejected-ответы и
        /// подробные сверки в обработчиках.</summary>
        public static bool VerifyMode = true;

        /// <summary>Сколько клиентов мультиклиент-хост может обеспечивать единовременно.</summary>
        public static int MulticlientCapacity = 24;

        // ── Тайминги ──
        public static int RollIntervalMs = 60;        // авторитарный роллинг сервера
        public static int GameTickMs = 25;            // серверный игровой тик (мины/рестарты)
        public static int ClientTickMs = 10;          // тик планировщика виртуальных клиентов
        public static int StateCheckIntervalMs = 900; // период StateCheck (verify mode)
        public static int CooldownToleranceMs = 200;  // допуск на сетевую задержку при проверке отката
        public static int ClientSpawnDelayMs = 120;   // растяжка коннектов/регистраций

        // ── Реестр живости (висящие инстансы) ──
        /// <summary>Период сверки клиентского реестра IECSObjectPath с сервером.</summary>
        public static int LivenessSweepMs = 1500;
        /// <summary>Возраст, после которого объект (мина / чужая сущность) считается подозрительным.</summary>
        public static int LivenessSuspectAgeMs = 6000;

        // ── Кинды нотисов ──
        public const string N_ServerReady = "SERVER_READY";
        public const string N_ServerFull = "SERVER_FULL";
        public const string N_JoinOk = "JOIN_OK";
        public const string N_JoinRejected = "JOIN_REJECTED";
        public const string N_LeaveOk = "LEAVE_OK";

        // ── Утилиты ──
        public static long Uid<T>() { return typeof(T).TypeId(); }
        public static long NowMs { get { return Environment.TickCount64; } }

        /// <summary>Интервал между выстрелами игрока (по всем пушкам), мс.</summary>
        public static double ShotIntervalMs { get { return 1000.0 / Math.Max(0.001, ShotsPerSecond); } }

        /// <summary>Откат ОДНОЙ пушки, мс: частота распределена по всем пушкам.</summary>
        public static double PerGunCooldownMs { get { return ShotIntervalMs * GunsPerPlayer; } }

        /// <summary>Откат способности мины, мс.</summary>
        public static double MineCooldownMs { get { return 1000.0 / Math.Max(0.001, MinePlacesPerSecond); } }

        /// <summary>Цена улучшения пушки уровня level → level+1.</summary>
        public static long UpgradeCost(int level)
        {
            return (long)Math.Round(UpgradeBaseCost * Math.Pow(UpgradeCostGrowth, level));
        }

        /// <summary>Урон пушки уровня level (без модификатора сессии).</summary>
        public static int GunDamage(int level)
        {
            return GunBaseDamage + level * GunDamagePerLevel;
        }

        /// <summary>
        /// Переопределение static-полей из окружения: AECC_LOAD_&lt;ИМЯ ПОЛЯ В ВЕРХНЕМ РЕГИСТРЕ&gt;.
        /// Пример: AECC_LOAD_VERIFYMODE=false AECC_LOAD_MAXSESSIONSONSERVER=10
        /// </summary>
        public static void ApplyEnvOverrides()
        {
            foreach (var f in typeof(LK).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
            {
                if (f.IsLiteral || f.IsInitOnly) continue;
                var env = Environment.GetEnvironmentVariable("AECC_LOAD_" + f.Name.ToUpperInvariant());
                if (string.IsNullOrEmpty(env)) continue;
                try
                {
                    if (f.FieldType == typeof(int)) f.SetValue(null, int.Parse(env));
                    else if (f.FieldType == typeof(long)) f.SetValue(null, long.Parse(env));
                    else if (f.FieldType == typeof(double)) f.SetValue(null, double.Parse(env, System.Globalization.CultureInfo.InvariantCulture));
                    else if (f.FieldType == typeof(bool)) f.SetValue(null, bool.Parse(env));
                    Console.WriteLine("[LK] override " + f.Name + " = " + f.GetValue(null));
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[LK] bad override " + f.Name + "=" + env + ": " + ex.Message);
                }
            }
            // задел на 32 пушки — жёсткий потолок ёмкости операционных компонентов
            if (GunsPerPlayer > MaxOperationalComponents) GunsPerPlayer = MaxOperationalComponents;
            if (GunsPerPlayer < 1) GunsPerPlayer = 1;
            if (SessionKillTargetMin < 100) SessionKillTargetMin = 100;   // «не менее 100»
        }
    }
}
