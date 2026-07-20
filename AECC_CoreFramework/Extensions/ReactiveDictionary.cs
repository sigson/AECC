using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;

// ============================================================================
//  ReactiveDictionary — самодостаточный порт UniRx.ReactiveDictionary
//  (UniRx-master/Assets/Plugins/UniRx/Scripts/UnityEngineBridge/ReactiveDictionary.cs)
//
//  Отличия от оригинала:
//   * НЕТ зависимостей от UnityEngine / Godot / System.Reactive — весь Rx-минимум
//     (Unit, Subject<T>, Observable.Empty, StartWith, Subscribe, CompositeDisposable)
//     упакован прямо в этот файл. C# 7.3 / netstandard2.0 / net472.
//   * Убраны ISerializable/IDeserializationCallback: UniRx объявлял их без
//     конструктора десериализации, т.е. round-trip всё равно падал.
//   * ДОБАВЛЕНО типизированное API поверх словаря, хранящего object:
//       TryGetValue<T>(key, out T)          — дженериком
//       TryGetValue(key, typeof(T), out object) — через Type
//       Get<T> / GetOrDefault<T>
//       ObserveValue<T>(key, callback)      — регистрация коллбека на ключ
//       ObserveValue(key, Type, callback)
//     Конвертация: прямой каст → Nullable<> → enum (имя или число) → IConvertible
//     (int→float, double→float и т.п. — важно для значений, пришедших из JSON).
//
//  Файл побайтово одинаков на клиенте (DotTanks/ClientCore/AECC/...) и сервере
//  (DotTanksServer/AECC/...) — правки вносить синхронно в обе копии.
// ============================================================================

namespace AECC.Extensions
{
    #region Rx-минимум (Unit / Disposable / Subject / Observable)

    /// <summary>Пустое значение — аналог void для Rx-потоков.</summary>
    public struct Unit : IEquatable<Unit>
    {
        static readonly Unit @default = new Unit();

        public static Unit Default { get { return @default; } }

        public bool Equals(Unit other) { return true; }
        public override bool Equals(object obj) { return obj is Unit; }
        public override int GetHashCode() { return 0; }
        public override string ToString() { return "()"; }

        public static bool operator ==(Unit first, Unit second) { return true; }
        public static bool operator !=(Unit first, Unit second) { return false; }
    }

    /// <summary>Хелперы IDisposable.</summary>
    public static class Disposable
    {
        sealed class EmptyDisposable : IDisposable
        {
            public static readonly EmptyDisposable Singleton = new EmptyDisposable();
            EmptyDisposable() { }
            public void Dispose() { }
        }

        sealed class AnonymousDisposable : IDisposable
        {
            Action dispose;
            public AnonymousDisposable(Action dispose) { this.dispose = dispose; }

            public void Dispose()
            {
                // Interlocked — Dispose обязан быть идемпотентным и потокобезопасным.
                var action = Interlocked.Exchange(ref dispose, null);
                if (action != null) action();
            }
        }

        public static IDisposable Empty { get { return EmptyDisposable.Singleton; } }

        public static IDisposable Create(Action dispose)
        {
            if (dispose == null) throw new ArgumentNullException("dispose");
            return new AnonymousDisposable(dispose);
        }
    }

    /// <summary>Набор подписок, снимаемых одним Dispose.</summary>
    public sealed class CompositeDisposable : IDisposable
    {
        readonly object gate = new object();
        List<IDisposable> disposables = new List<IDisposable>();
        bool disposed;

        public void Add(IDisposable disposable)
        {
            if (disposable == null) return;
            lock (gate)
            {
                if (!disposed)
                {
                    disposables.Add(disposable);
                    return;
                }
            }
            // Добавление в уже освобождённый набор — сразу гасим подписку.
            disposable.Dispose();
        }

        public void Dispose()
        {
            List<IDisposable> toDispose;
            lock (gate)
            {
                if (disposed) return;
                disposed = true;
                toDispose = disposables;
                disposables = null;
            }
            for (int i = 0; i < toDispose.Count; i++) toDispose[i].Dispose();
        }
    }

    /// <summary>
    /// Минимальный Subject: горячий источник, раздающий OnNext всем подписчикам.
    /// Список наблюдателей — copy-on-write, чтобы OnNext не держал блокировку и
    /// подписка/отписка изнутри коллбека не ломала итерацию.
    /// </summary>
    public sealed class Subject<T> : IObservable<T>, IObserver<T>, IDisposable
    {
        static readonly IObserver<T>[] Empty = new IObserver<T>[0];

        readonly object gate = new object();
        volatile IObserver<T>[] observers = Empty;
        bool isStopped;
        bool isDisposed;
        Exception lastError;

        public bool HasObservers { get { return observers.Length > 0; } }

        public void OnNext(T value)
        {
            var snapshot = observers;
            for (int i = 0; i < snapshot.Length; i++) snapshot[i].OnNext(value);
        }

        public void OnError(Exception error)
        {
            if (error == null) throw new ArgumentNullException("error");

            IObserver<T>[] snapshot;
            lock (gate)
            {
                if (isStopped) return;
                isStopped = true;
                lastError = error;
                snapshot = observers;
                observers = Empty;
            }
            for (int i = 0; i < snapshot.Length; i++) snapshot[i].OnError(error);
        }

        public void OnCompleted()
        {
            IObserver<T>[] snapshot;
            lock (gate)
            {
                if (isStopped) return;
                isStopped = true;
                snapshot = observers;
                observers = Empty;
            }
            for (int i = 0; i < snapshot.Length; i++) snapshot[i].OnCompleted();
        }

        public IDisposable Subscribe(IObserver<T> observer)
        {
            if (observer == null) throw new ArgumentNullException("observer");

            lock (gate)
            {
                if (isDisposed) throw new ObjectDisposedException("Subject");

                if (!isStopped)
                {
                    var current = observers;
                    var next = new IObserver<T>[current.Length + 1];
                    Array.Copy(current, next, current.Length);
                    next[current.Length] = observer;
                    observers = next;

                    return Disposable.Create(() => Unsubscribe(observer));
                }

                if (lastError != null) observer.OnError(lastError);
                else observer.OnCompleted();
                return Disposable.Empty;
            }
        }

        void Unsubscribe(IObserver<T> observer)
        {
            lock (gate)
            {
                var current = observers;
                var index = Array.IndexOf(current, observer);
                if (index < 0) return;

                if (current.Length == 1)
                {
                    observers = Empty;
                    return;
                }

                var next = new IObserver<T>[current.Length - 1];
                Array.Copy(current, 0, next, 0, index);
                Array.Copy(current, index + 1, next, index, current.Length - index - 1);
                observers = next;
            }
        }

        public void Dispose()
        {
            lock (gate)
            {
                isDisposed = true;
                observers = Empty;
                lastError = null;
            }
        }
    }

    /// <summary>Фабрики Observable — ровно то, что нужно словарю.</summary>
    public static class Observable
    {
        sealed class EmptyObservable<T> : IObservable<T>
        {
            public static readonly EmptyObservable<T> Singleton = new EmptyObservable<T>();
            EmptyObservable() { }

            public IDisposable Subscribe(IObserver<T> observer)
            {
                if (observer == null) throw new ArgumentNullException("observer");
                observer.OnCompleted();
                return Disposable.Empty;
            }
        }

        sealed class AnonymousObservable<T> : IObservable<T>
        {
            readonly Func<IObserver<T>, IDisposable> subscribe;
            public AnonymousObservable(Func<IObserver<T>, IDisposable> subscribe) { this.subscribe = subscribe; }
            public IDisposable Subscribe(IObserver<T> observer) { return subscribe(observer); }
        }

        public static IObservable<T> Empty<T>() { return EmptyObservable<T>.Singleton; }

        public static IObservable<T> Create<T>(Func<IObserver<T>, IDisposable> subscribe)
        {
            if (subscribe == null) throw new ArgumentNullException("subscribe");
            return new AnonymousObservable<T>(subscribe);
        }
    }

    /// <summary>Операторы, используемые словарём и вызывающим кодом.</summary>
    public static class ObservableExtensions
    {
        sealed class AnonymousObserver<T> : IObserver<T>
        {
            readonly Action<T> onNext;
            readonly Action<Exception> onError;
            readonly Action onCompleted;

            public AnonymousObserver(Action<T> onNext, Action<Exception> onError, Action onCompleted)
            {
                this.onNext = onNext;
                this.onError = onError;
                this.onCompleted = onCompleted;
            }

            public void OnNext(T value) { if (onNext != null) onNext(value); }
            public void OnError(Exception error) { if (onError != null) onError(error); }
            public void OnCompleted() { if (onCompleted != null) onCompleted(); }
        }

        public static IDisposable Subscribe<T>(this IObservable<T> source, Action<T> onNext)
        {
            if (source == null) throw new ArgumentNullException("source");
            if (onNext == null) throw new ArgumentNullException("onNext");
            return source.Subscribe(new AnonymousObserver<T>(onNext, null, null));
        }

        public static IDisposable Subscribe<T>(this IObservable<T> source, Action<T> onNext, Action<Exception> onError)
        {
            if (source == null) throw new ArgumentNullException("source");
            return source.Subscribe(new AnonymousObserver<T>(onNext, onError, null));
        }

        public static IDisposable Subscribe<T>(this IObservable<T> source, Action<T> onNext, Action<Exception> onError, Action onCompleted)
        {
            if (source == null) throw new ArgumentNullException("source");
            return source.Subscribe(new AnonymousObserver<T>(onNext, onError, onCompleted));
        }

        /// <summary>Отдаёт значение фабрики в момент подписки, затем поток источника.</summary>
        public static IObservable<T> StartWith<T>(this IObservable<T> source, Func<T> valueFactory)
        {
            if (source == null) throw new ArgumentNullException("source");
            if (valueFactory == null) throw new ArgumentNullException("valueFactory");

            return Observable.Create<T>(observer =>
            {
                observer.OnNext(valueFactory());
                return source.Subscribe(observer);
            });
        }

        public static IObservable<T> StartWith<T>(this IObservable<T> source, T value)
        {
            return source.StartWith(() => value);
        }

        public static IObservable<T> Where<T>(this IObservable<T> source, Func<T, bool> predicate)
        {
            if (source == null) throw new ArgumentNullException("source");
            if (predicate == null) throw new ArgumentNullException("predicate");

            return Observable.Create<T>(observer => source.Subscribe(
                value => { if (predicate(value)) observer.OnNext(value); },
                observer.OnError,
                observer.OnCompleted));
        }

        public static IObservable<TResult> Select<T, TResult>(this IObservable<T> source, Func<T, TResult> selector)
        {
            if (source == null) throw new ArgumentNullException("source");
            if (selector == null) throw new ArgumentNullException("selector");

            return Observable.Create<TResult>(observer => source.Subscribe(
                value => observer.OnNext(selector(value)),
                observer.OnError,
                observer.OnCompleted));
        }
    }

    #endregion

    #region Конвертация значений (для словаря object-ов)

    /// <summary>
    /// Приведение object-значения словаря к запрошенному типу. Используется и
    /// дженерик-, и Type-вариантами API, чтобы правила были ровно одни.
    ///
    /// Конверсия НАМЕРЕННО зажата: голый Convert.ChangeType по IConvertible
    /// молча превращает true в 1, а любое значение — в строку, из-за чего запрос
    /// не того типа возвращал бы мусор вместо false. Разрешено ровно два случая:
    ///   * число → число (int/float/double/... — разнобой ширины из JSON/XML);
    ///   * строка → примитив (значения из текстовых конфигов: "true", "0.5").
    /// bool ⇄ число и «что угодно → string» запрещены.
    /// </summary>
    public static class ReactiveValueConverter
    {
        static readonly HashSet<Type> NumericTypes = new HashSet<Type>
        {
            typeof(sbyte), typeof(byte), typeof(short), typeof(ushort),
            typeof(int), typeof(uint), typeof(long), typeof(ulong),
            typeof(float), typeof(double), typeof(decimal)
        };

        static bool IsNumeric(Type type) { return NumericTypes.Contains(type); }

        public static bool TryConvert(object raw, Type targetType, out object result)
        {
            if (targetType == null) throw new ArgumentNullException("targetType");

            var underlying = Nullable.GetUnderlyingType(targetType) ?? targetType;

            if (raw == null)
            {
                result = null;
                // null допустим только для ссылочных типов и Nullable<>.
                return !targetType.IsValueType || Nullable.GetUnderlyingType(targetType) != null;
            }

            if (underlying.IsInstanceOfType(raw))
            {
                result = raw;
                return true;
            }

            var sourceType = raw.GetType();
            var asString = raw as string;

            try
            {
                if (underlying.IsEnum)
                {
                    if (asString != null)
                    {
                        result = Enum.Parse(underlying, asString, true);
                        return true;
                    }
                    if (IsNumeric(sourceType))
                    {
                        result = Enum.ToObject(underlying, raw);
                        return true;
                    }
                    result = null;
                    return false;
                }

                // Число → число: сглаживаем разнобой ширины (int из JSON в float-настройке).
                if (IsNumeric(sourceType) && IsNumeric(underlying))
                {
                    result = Convert.ChangeType(raw, underlying, CultureInfo.InvariantCulture);
                    return true;
                }

                // Строка → примитив: разбор значений из текстовых конфигов.
                if (asString != null && (IsNumeric(underlying) || underlying == typeof(bool) || underlying == typeof(char)))
                {
                    result = Convert.ChangeType(asString, underlying, CultureInfo.InvariantCulture);
                    return true;
                }
            }
            catch (InvalidCastException) { }
            catch (FormatException) { }
            catch (OverflowException) { }
            catch (ArgumentException) { }

            result = null;
            return false;
        }

        public static bool TryConvert<TResult>(object raw, out TResult result)
        {
            if (raw is TResult direct)
            {
                result = direct;
                return true;
            }

            object converted;
            if (TryConvert(raw, typeof(TResult), out converted))
            {
                result = converted == null ? default(TResult) : (TResult)converted;
                return true;
            }

            result = default(TResult);
            return false;
        }
    }

    #endregion

    #region События словаря

    public struct DictionaryAddEvent<TKey, TValue> : IEquatable<DictionaryAddEvent<TKey, TValue>>
    {
        public TKey Key { get; private set; }
        public TValue Value { get; private set; }

        public DictionaryAddEvent(TKey key, TValue value) : this()
        {
            Key = key;
            Value = value;
        }

        public override string ToString()
        {
            return string.Format("Key:{0} Value:{1}", Key, Value);
        }

        public override int GetHashCode()
        {
            return EqualityComparer<TKey>.Default.GetHashCode(Key) ^ EqualityComparer<TValue>.Default.GetHashCode(Value) << 2;
        }

        public override bool Equals(object obj)
        {
            return obj is DictionaryAddEvent<TKey, TValue> && Equals((DictionaryAddEvent<TKey, TValue>)obj);
        }

        public bool Equals(DictionaryAddEvent<TKey, TValue> other)
        {
            return EqualityComparer<TKey>.Default.Equals(Key, other.Key)
                && EqualityComparer<TValue>.Default.Equals(Value, other.Value);
        }
    }

    public struct DictionaryRemoveEvent<TKey, TValue> : IEquatable<DictionaryRemoveEvent<TKey, TValue>>
    {
        public TKey Key { get; private set; }
        public TValue Value { get; private set; }

        public DictionaryRemoveEvent(TKey key, TValue value) : this()
        {
            Key = key;
            Value = value;
        }

        public override string ToString()
        {
            return string.Format("Key:{0} Value:{1}", Key, Value);
        }

        public override int GetHashCode()
        {
            return EqualityComparer<TKey>.Default.GetHashCode(Key) ^ EqualityComparer<TValue>.Default.GetHashCode(Value) << 2;
        }

        public override bool Equals(object obj)
        {
            return obj is DictionaryRemoveEvent<TKey, TValue> && Equals((DictionaryRemoveEvent<TKey, TValue>)obj);
        }

        public bool Equals(DictionaryRemoveEvent<TKey, TValue> other)
        {
            return EqualityComparer<TKey>.Default.Equals(Key, other.Key)
                && EqualityComparer<TValue>.Default.Equals(Value, other.Value);
        }
    }

    public struct DictionaryReplaceEvent<TKey, TValue> : IEquatable<DictionaryReplaceEvent<TKey, TValue>>
    {
        public TKey Key { get; private set; }
        public TValue OldValue { get; private set; }
        public TValue NewValue { get; private set; }

        public DictionaryReplaceEvent(TKey key, TValue oldValue, TValue newValue) : this()
        {
            Key = key;
            OldValue = oldValue;
            NewValue = newValue;
        }

        public override string ToString()
        {
            return string.Format("Key:{0} OldValue:{1} NewValue:{2}", Key, OldValue, NewValue);
        }

        public override int GetHashCode()
        {
            return EqualityComparer<TKey>.Default.GetHashCode(Key)
                ^ EqualityComparer<TValue>.Default.GetHashCode(OldValue) << 2
                ^ EqualityComparer<TValue>.Default.GetHashCode(NewValue) >> 2;
        }

        public override bool Equals(object obj)
        {
            return obj is DictionaryReplaceEvent<TKey, TValue> && Equals((DictionaryReplaceEvent<TKey, TValue>)obj);
        }

        public bool Equals(DictionaryReplaceEvent<TKey, TValue> other)
        {
            return EqualityComparer<TKey>.Default.Equals(Key, other.Key)
                && EqualityComparer<TValue>.Default.Equals(OldValue, other.OldValue)
                && EqualityComparer<TValue>.Default.Equals(NewValue, other.NewValue);
        }
    }

    #endregion

    #region Интерфейсы

    public interface IReadOnlyReactiveDictionary<TKey, TValue> : IEnumerable<KeyValuePair<TKey, TValue>>
    {
        int Count { get; }
        TValue this[TKey index] { get; }
        bool ContainsKey(TKey key);
        bool TryGetValue(TKey key, out TValue value);

        IObservable<DictionaryAddEvent<TKey, TValue>> ObserveAdd();
        IObservable<int> ObserveCountChanged(bool notifyCurrentCount = false);
        IObservable<DictionaryRemoveEvent<TKey, TValue>> ObserveRemove();
        IObservable<DictionaryReplaceEvent<TKey, TValue>> ObserveReplace();
        IObservable<Unit> ObserveReset();
    }

    public interface IReactiveDictionary<TKey, TValue> : IReadOnlyReactiveDictionary<TKey, TValue>, IDictionary<TKey, TValue>
    {
    }

    #endregion

    [Serializable]
    public class ReactiveDictionary<TKey, TValue> :
        IReactiveDictionary<TKey, TValue>,
        IDictionary<TKey, TValue>,
        IEnumerable,
        ICollection<KeyValuePair<TKey, TValue>>,
        IEnumerable<KeyValuePair<TKey, TValue>>,
        IDictionary,
        IDisposable
    {
        [NonSerialized]
        bool isDisposed = false;

        readonly Dictionary<TKey, TValue> inner;

        public ReactiveDictionary()
        {
            inner = new Dictionary<TKey, TValue>();
        }

        public ReactiveDictionary(IEqualityComparer<TKey> comparer)
        {
            inner = new Dictionary<TKey, TValue>(comparer);
        }

        public ReactiveDictionary(Dictionary<TKey, TValue> innerDictionary)
        {
            inner = innerDictionary;
        }

        public TValue this[TKey key]
        {
            get { return inner[key]; }
            set
            {
                TValue oldValue;
                if (TryGetValue(key, out oldValue))
                {
                    inner[key] = value;
                    if (dictionaryReplace != null) dictionaryReplace.OnNext(new DictionaryReplaceEvent<TKey, TValue>(key, oldValue, value));
                }
                else
                {
                    inner[key] = value;
                    if (dictionaryAdd != null) dictionaryAdd.OnNext(new DictionaryAddEvent<TKey, TValue>(key, value));
                    if (countChanged != null) countChanged.OnNext(Count);
                }
            }
        }

        public int Count { get { return inner.Count; } }

        public Dictionary<TKey, TValue>.KeyCollection Keys { get { return inner.Keys; } }

        public Dictionary<TKey, TValue>.ValueCollection Values { get { return inner.Values; } }

        public void Add(TKey key, TValue value)
        {
            inner.Add(key, value);

            if (dictionaryAdd != null) dictionaryAdd.OnNext(new DictionaryAddEvent<TKey, TValue>(key, value));
            if (countChanged != null) countChanged.OnNext(Count);
        }

        public void Clear()
        {
            var beforeCount = Count;
            inner.Clear();

            if (collectionReset != null) collectionReset.OnNext(Unit.Default);
            if (beforeCount > 0)
            {
                if (countChanged != null) countChanged.OnNext(Count);
            }
        }

        public bool Remove(TKey key)
        {
            TValue oldValue;
            if (inner.TryGetValue(key, out oldValue))
            {
                var isSuccessRemove = inner.Remove(key);
                if (isSuccessRemove)
                {
                    if (dictionaryRemove != null) dictionaryRemove.OnNext(new DictionaryRemoveEvent<TKey, TValue>(key, oldValue));
                    if (countChanged != null) countChanged.OnNext(Count);
                }
                return isSuccessRemove;
            }
            return false;
        }

        public bool ContainsKey(TKey key)
        {
            return inner.ContainsKey(key);
        }

        public bool TryGetValue(TKey key, out TValue value)
        {
            return inner.TryGetValue(key, out value);
        }

        public Dictionary<TKey, TValue>.Enumerator GetEnumerator()
        {
            return inner.GetEnumerator();
        }

        #region Типизированный доступ (двойственное API: дженерик или Type)

        /// <summary>
        /// Достаёт значение и приводит к <typeparamref name="TResult"/>:
        /// <c>settings.TryGetValue&lt;bool&gt;("SoundActive", out var sound)</c>.
        /// Возвращает false, если ключа нет ИЛИ значение не приводится к типу.
        /// </summary>
        public bool TryGetValue<TResult>(TKey key, out TResult value)
        {
            TValue raw;
            if (inner.TryGetValue(key, out raw))
            {
                return ReactiveValueConverter.TryConvert(raw, out value);
            }

            value = default(TResult);
            return false;
        }

        /// <summary>
        /// То же самое, но тип задаётся объектом <see cref="Type"/>
        /// (<c>typeof(bool)</c> или вычисленный в рантайме).
        /// </summary>
        public bool TryGetValue(TKey key, Type expectedType, out object value)
        {
            TValue raw;
            if (inner.TryGetValue(key, out raw))
            {
                return ReactiveValueConverter.TryConvert(raw, expectedType, out value);
            }

            value = null;
            return false;
        }

        /// <summary>Значение по ключу или <paramref name="fallback"/>, если ключа нет / тип не тот.</summary>
        public TResult GetOrDefault<TResult>(TKey key, TResult fallback = default(TResult))
        {
            TResult value;
            return TryGetValue(key, out value) ? value : fallback;
        }

        public object GetOrDefault(TKey key, Type expectedType, object fallback = null)
        {
            object value;
            return TryGetValue(key, expectedType, out value) ? value : fallback;
        }

        /// <summary>Строгий доступ: бросает, если ключа нет или тип не приводится.</summary>
        public TResult Get<TResult>(TKey key)
        {
            TValue raw;
            if (!inner.TryGetValue(key, out raw))
                throw new KeyNotFoundException(string.Format("ReactiveDictionary: ключ '{0}' не найден.", key));

            TResult value;
            if (!ReactiveValueConverter.TryConvert(raw, out value))
                throw new InvalidCastException(string.Format(
                    "ReactiveDictionary: значение ключа '{0}' типа {1} не приводится к {2}.",
                    key, raw == null ? "null" : raw.GetType().Name, typeof(TResult).Name));

            return value;
        }

        public object Get(TKey key, Type expectedType)
        {
            TValue raw;
            if (!inner.TryGetValue(key, out raw))
                throw new KeyNotFoundException(string.Format("ReactiveDictionary: ключ '{0}' не найден.", key));

            object value;
            if (!ReactiveValueConverter.TryConvert(raw, expectedType, out value))
                throw new InvalidCastException(string.Format(
                    "ReactiveDictionary: значение ключа '{0}' типа {1} не приводится к {2}.",
                    key, raw == null ? "null" : raw.GetType().Name, expectedType.Name));

            return value;
        }

        #endregion

        #region Подписка на конкретный ключ (регистрация коллбеков)

        /// <summary>
        /// Вызывает <paramref name="onValue"/> при каждой установке значения по
        /// ключу (и Add, и Replace). По умолчанию сразу отдаёт текущее значение,
        /// если оно уже есть — подписчику не нужно отдельно инициализироваться.
        /// Значения, не приводящиеся к <typeparamref name="TResult"/>, игнорируются.
        /// Возвращённый IDisposable снимает подписку.
        /// </summary>
        public IDisposable ObserveValue<TResult>(TKey key, Action<TResult> onValue, bool notifyCurrentValue = true)
        {
            if (onValue == null) throw new ArgumentNullException("onValue");

            var comparer = EqualityComparer<TKey>.Default;
            var subscriptions = new CompositeDisposable();

            subscriptions.Add(ObserveAdd().Subscribe(e =>
            {
                if (!comparer.Equals(e.Key, key)) return;
                TResult typed;
                if (ReactiveValueConverter.TryConvert(e.Value, out typed)) onValue(typed);
            }));

            subscriptions.Add(ObserveReplace().Subscribe(e =>
            {
                if (!comparer.Equals(e.Key, key)) return;
                TResult typed;
                if (ReactiveValueConverter.TryConvert(e.NewValue, out typed)) onValue(typed);
            }));

            if (notifyCurrentValue)
            {
                TResult current;
                if (TryGetValue(key, out current)) onValue(current);
            }

            return subscriptions;
        }

        /// <summary>Вариант с типом через <see cref="Type"/> вместо дженерика.</summary>
        public IDisposable ObserveValue(TKey key, Type expectedType, Action<object> onValue, bool notifyCurrentValue = true)
        {
            if (expectedType == null) throw new ArgumentNullException("expectedType");
            if (onValue == null) throw new ArgumentNullException("onValue");

            var comparer = EqualityComparer<TKey>.Default;
            var subscriptions = new CompositeDisposable();

            subscriptions.Add(ObserveAdd().Subscribe(e =>
            {
                if (!comparer.Equals(e.Key, key)) return;
                object typed;
                if (ReactiveValueConverter.TryConvert(e.Value, expectedType, out typed)) onValue(typed);
            }));

            subscriptions.Add(ObserveReplace().Subscribe(e =>
            {
                if (!comparer.Equals(e.Key, key)) return;
                object typed;
                if (ReactiveValueConverter.TryConvert(e.NewValue, expectedType, out typed)) onValue(typed);
            }));

            if (notifyCurrentValue)
            {
                object current;
                if (TryGetValue(key, expectedType, out current)) onValue(current);
            }

            return subscriptions;
        }

        #endregion

        void DisposeSubject<TSubject>(ref Subject<TSubject> subject)
        {
            if (subject != null)
            {
                try
                {
                    subject.OnCompleted();
                }
                finally
                {
                    subject.Dispose();
                    subject = null;
                }
            }
        }

        #region IDisposable Support

        private bool disposedValue = false;

        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    isDisposed = true;
                    DisposeSubject(ref countChanged);
                    DisposeSubject(ref collectionReset);
                    DisposeSubject(ref dictionaryAdd);
                    DisposeSubject(ref dictionaryRemove);
                    DisposeSubject(ref dictionaryReplace);
                }

                disposedValue = true;
            }
        }

        public void Dispose()
        {
            Dispose(true);
        }

        #endregion

        #region Observe

        [NonSerialized]
        Subject<int> countChanged = null;
        public IObservable<int> ObserveCountChanged(bool notifyCurrentCount = false)
        {
            if (isDisposed) return Observable.Empty<int>();

            var subject = countChanged ?? (countChanged = new Subject<int>());
            if (notifyCurrentCount)
            {
                return subject.StartWith(() => this.Count);
            }
            return subject;
        }

        [NonSerialized]
        Subject<Unit> collectionReset = null;
        public IObservable<Unit> ObserveReset()
        {
            if (isDisposed) return Observable.Empty<Unit>();
            return collectionReset ?? (collectionReset = new Subject<Unit>());
        }

        [NonSerialized]
        Subject<DictionaryAddEvent<TKey, TValue>> dictionaryAdd = null;
        public IObservable<DictionaryAddEvent<TKey, TValue>> ObserveAdd()
        {
            if (isDisposed) return Observable.Empty<DictionaryAddEvent<TKey, TValue>>();
            return dictionaryAdd ?? (dictionaryAdd = new Subject<DictionaryAddEvent<TKey, TValue>>());
        }

        [NonSerialized]
        Subject<DictionaryRemoveEvent<TKey, TValue>> dictionaryRemove = null;
        public IObservable<DictionaryRemoveEvent<TKey, TValue>> ObserveRemove()
        {
            if (isDisposed) return Observable.Empty<DictionaryRemoveEvent<TKey, TValue>>();
            return dictionaryRemove ?? (dictionaryRemove = new Subject<DictionaryRemoveEvent<TKey, TValue>>());
        }

        [NonSerialized]
        Subject<DictionaryReplaceEvent<TKey, TValue>> dictionaryReplace = null;
        public IObservable<DictionaryReplaceEvent<TKey, TValue>> ObserveReplace()
        {
            if (isDisposed) return Observable.Empty<DictionaryReplaceEvent<TKey, TValue>>();
            return dictionaryReplace ?? (dictionaryReplace = new Subject<DictionaryReplaceEvent<TKey, TValue>>());
        }

        #endregion

        #region implement explicit

        object IDictionary.this[object key]
        {
            get { return this[(TKey)key]; }
            set { this[(TKey)key] = (TValue)value; }
        }

        bool IDictionary.IsFixedSize { get { return ((IDictionary)inner).IsFixedSize; } }

        bool IDictionary.IsReadOnly { get { return ((IDictionary)inner).IsReadOnly; } }

        bool ICollection.IsSynchronized { get { return ((IDictionary)inner).IsSynchronized; } }

        ICollection IDictionary.Keys { get { return ((IDictionary)inner).Keys; } }

        object ICollection.SyncRoot { get { return ((IDictionary)inner).SyncRoot; } }

        ICollection IDictionary.Values { get { return ((IDictionary)inner).Values; } }

        bool ICollection<KeyValuePair<TKey, TValue>>.IsReadOnly
        {
            get { return ((ICollection<KeyValuePair<TKey, TValue>>)inner).IsReadOnly; }
        }

        ICollection<TKey> IDictionary<TKey, TValue>.Keys { get { return inner.Keys; } }

        ICollection<TValue> IDictionary<TKey, TValue>.Values { get { return inner.Values; } }

        void IDictionary.Add(object key, object value)
        {
            Add((TKey)key, (TValue)value);
        }

        bool IDictionary.Contains(object key)
        {
            return ((IDictionary)inner).Contains(key);
        }

        void ICollection.CopyTo(Array array, int index)
        {
            ((IDictionary)inner).CopyTo(array, index);
        }

        void IDictionary.Remove(object key)
        {
            Remove((TKey)key);
        }

        void ICollection<KeyValuePair<TKey, TValue>>.Add(KeyValuePair<TKey, TValue> item)
        {
            Add(item.Key, item.Value);
        }

        bool ICollection<KeyValuePair<TKey, TValue>>.Contains(KeyValuePair<TKey, TValue> item)
        {
            return ((ICollection<KeyValuePair<TKey, TValue>>)inner).Contains(item);
        }

        void ICollection<KeyValuePair<TKey, TValue>>.CopyTo(KeyValuePair<TKey, TValue>[] array, int arrayIndex)
        {
            ((ICollection<KeyValuePair<TKey, TValue>>)inner).CopyTo(array, arrayIndex);
        }

        IEnumerator<KeyValuePair<TKey, TValue>> IEnumerable<KeyValuePair<TKey, TValue>>.GetEnumerator()
        {
            return ((ICollection<KeyValuePair<TKey, TValue>>)inner).GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return inner.GetEnumerator();
        }

        bool ICollection<KeyValuePair<TKey, TValue>>.Remove(KeyValuePair<TKey, TValue> item)
        {
            TValue v;
            if (TryGetValue(item.Key, out v))
            {
                if (EqualityComparer<TValue>.Default.Equals(v, item.Value))
                {
                    Remove(item.Key);
                    return true;
                }
            }

            return false;
        }

        IDictionaryEnumerator IDictionary.GetEnumerator()
        {
            return ((IDictionary)inner).GetEnumerator();
        }

        #endregion
    }

    public static partial class ReactiveDictionaryExtensions
    {
        public static ReactiveDictionary<TKey, TValue> ToReactiveDictionary<TKey, TValue>(this Dictionary<TKey, TValue> dictionary)
        {
            return new ReactiveDictionary<TKey, TValue>(dictionary);
        }
    }
}
