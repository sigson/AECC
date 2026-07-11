using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AECC.Core.BuiltInTypes.Types.AtomicType
{
    [System.Serializable]
    [TypeUid(106)]
    public class DeterministicContainer : BaseCustomType
    {
        static public new long Id { get; set; } = 106;
        public long _salt = 0;

        public DeterministicContainer()
        {
            
        }

        public DeterministicContainer(long salt)
        {
            _salt = salt;
        }

        /// <summary>
        /// Создает экземпляр процессора с указанной солью.
        /// </summary>
        /// <param name="salt">Значение long, используемое для инициализации генератора 
        /// случайных чисел.</param>
        public DeterministicContainer SetSalt(long salt)
        {
            _salt = salt;
            return this;
        }

        /// <summary>
        /// Вспомогательный метод для получения int "seed" из long "salt".
        /// GetHashCode() для long хорошо смешивает биты.
        /// </summary>
        private int GetSeed() => _salt.GetHashCode();

        /// <summary>
        /// Детерминированно фильтрует коллекцию на основе соли.
        /// Элементы будут либо включены, либо исключены с предсказуемой вероятностью.
        /// </summary>
        /// <typeparam name="T">Тип элементов в коллекции.</typeparam>
        /// <param name="collection">Исходная коллекция.</param>
        /// <returns>Новый массив, содержащий отфильтрованные элементы.</returns>
        public T[] DeterministicFilter<T>(IEnumerable<T> collection, int seedInject = 0)
        {
            var rand = new Random(GetSeed() + seedInject);
            var filteredList = new List<T>();

            foreach (var item in collection)
            {
                if (rand.NextDouble() >= 0.5)
                {
                    filteredList.Add(item);
                }
            }

            return filteredList.ToArray();
        }

        /// <summary>
        /// Детерминированно "перемешивает" элементы коллекции в новом порядке
        /// на основе соли, используя алгоритм тасования Фишера-Йейтса.
        /// </summary>
        /// <typeparam name="T">Тип элементов в коллекции.</typeparam>
        /// <param name="collection">Исходная коллекция.</param>
        /// <returns>Новый массив с элементами в "случайном", но
        /// детерминированном порядке.</returns>
        public T[] DeterministicShuffle<T>(IEnumerable<T> collection, int seedInject = 0)
        {
            var rand = new Random(GetSeed() + seedInject);

            var array = collection.ToArray();
            int n = array.Length;

            for (int i = n - 1; i > 0; i--)
            {
                int j = rand.Next(i + 1);

                T temp = array[i];
                array[i] = array[j];
                array[j] = temp;
            }

            return array;
        }

        /// <summary>
        /// Выполняет действие (Action) для каждого элемента коллекции, передавая
        /// в него сам элемент и детерминированное "случайное" long значение,
        /// сгенерированное на основе соли.
        /// </summary>
        /// <typeparam name="T">Тип элементов в коллекции.</typeparam>
        /// <param name="collection">Исходная коллекция.</param>
        /// <param name="action">Лямбда-функция или метод, принимающий
        /// (T item, long deterministicValue).</param>
        public void ProcessWithDeterministicLong<T>(IEnumerable<T> collection, Action<T, long> action, int seedInject = 0)
        {
            var rand = new Random(GetSeed() + seedInject);

            byte[] buffer = new byte[8];

            foreach (var item in collection)
            {
                rand.NextBytes(buffer);

                long deterministicValue = BitConverter.ToInt64(buffer, 0);

                action(item, deterministicValue);
            }
        }

        public T[] DeterministicSelect<T>(IEnumerable<T> collection, int count, int seedInject = 0)
        {
            if (collection == null || count <= 0)
            {
                return new T[0];
            }

            T[] shuffledArray = DeterministicShuffle(collection, seedInject);

            if (count >= shuffledArray.Length)
            {
                return shuffledArray;
            }

            return shuffledArray.Take(count).ToArray();
        }

        /// <summary>
        /// Генерирует детерминированное значение double в диапазоне [0, 1).
        /// </summary>
        public double DeterministicDouble(int seedInject = 0)
        {
            var rand = new Random(GetSeed() + seedInject);
            return rand.NextDouble();
        }

        /// <summary>
        /// Генерирует детерминированное значение double в указанном диапазоне [min, max).
        /// </summary>
        public double DeterministicRange(double min, double max, int seedInject = 0)
        {
            var rand = new Random(GetSeed() + seedInject);
            return min + (max - min) * rand.NextDouble();
        }

        /// <summary>
        /// Генерирует пару детерминированных значений double в диапазоне [0, 1).
        /// Полезно для генерации 2D координат.
        /// </summary>
        public (double x, double y) DeterministicPoint2D(int seedInject = 0)
        {
            var rand = new Random(GetSeed() + seedInject);
            return (rand.NextDouble(), rand.NextDouble());
        }
    }
}