using AECC.Core.Logging;
using AECC.Extensions;
using AECC.Extensions.ThreadingSync;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;

public static class SuperExtensions
{
    public static T CastAs<T>(this object obj) where T : class
    {
        return obj as T;
    }
}

namespace AECC.Extensions
{
    public static class ClassEx
    {
		public static float NextFloat(this Random random, float startFloat, float endFloat)
		{
			if (startFloat >= endFloat)
			{
				throw new ArgumentException("startFloat must be less than endFloat");
			}

			// Генерация случайного числа в диапазоне [0, 1)
			float randomValue = (float)random.NextDouble();

			// Масштабирование и смещение для получения числа в диапазоне [startFloat, endFloat)
			return startFloat + randomValue * (endFloat - startFloat);
		}
		
        public static string RandomString(this Random random, int countSymbols)
        {
            const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
            return new string(Enumerable.Repeat(chars, countSymbols)
                .Select(s => s[random.Next(s.Length)]).ToArray());
        }

        public static float FastFloat(this string str)
        {
            return float.Parse(str, System.Globalization.CultureInfo.InvariantCulture);
        }

        public static long GuidToLongR(this Guid guid)
        {
            return DateTime.UtcNow.Ticks + BitConverter.ToInt64(guid.ToByteArray(), 8);
        }
        public static long GuidToLong(this Guid guid)
        {
            return BitConverter.ToInt64(guid.ToByteArray(), 8);
        }

        public static Type IdToECSType(this long id)
        {
            return AECC.Core.TypeRegistry.Global.GetType(id);
        }

        /// <summary>Фаза 2, дефект 6.1: резолв мемоизирован в реестре — рефлексия один раз
        /// на тип за процесс вместо каждого обращения к компоненту по Type (Kid()).</summary>
        public static long TypeId(this Type id)
        {
            return AECC.Core.TypeRegistry.Global.GetId(id);
        }

        public static long IdToECSType(this Type id)
        {
            return AECC.Core.TypeRegistry.Global.GetRegisteredId(id);
        }

        public static Type NameToECSType(this string componentName)
        {
            return AECC.Core.TypeRegistry.Global.GetType(componentName);
        }

        public static long NameToECSId(this string componentName)
        {
            return componentName.NameToECSType().IdToECSType();
        }
    }

}
