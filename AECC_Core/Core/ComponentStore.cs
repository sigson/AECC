using System;
using System.Collections;
using System.Collections.Generic;
using AECC.Locking;

namespace AECC.Core
{
    /// <summary>
    /// Слушатель событий хранилища компонентов. Дисциплина «событий»: это НЕ .NET-event
    /// и не очередь — синхронный прямой вызов интерфейса, зафиксированного при
    /// конструировании store; параметры вместо args-объектов; без
    /// multicast-делегатов/замыканий/LINQ. Асинхронность/очередь запрещены.
    ///
    /// Порядок вызова: «сначала мутация словаря завершена, потом вызов» — методы
    /// вызываются ПОД write-локом ячейки, сразу после того как значение легло в слот.
    /// Реакции уровня хранилища (AddedReaction и т.п.) — ВНЕ структурного лока, их
    /// запускает оркестратор после возврата операции («сначала факт, потом событие»).
    ///
    /// Слушатель сейчас — EntityComponentStorage (оркестратор сериализационных зеркал);
    /// контекстные параметры restoringMode/silent/restoringOwner обслуживают зеркала.
    /// </summary>
    public interface IComponentStoreListener
    {
        /// <summary>Компонент лёг в слот (add-ветка). Под write-локом ячейки.</summary>
        void ComponentAdded(long typeUid, ECSComponent component, bool restoringMode);

        /// <summary>Компонент заменён в слоте (change-ветка). Под write-локом ячейки.
        /// Полный процесс изменения: владение/Unregistered/dirty-set.</summary>
        void ComponentChanged(long typeUid, ECSComponent component, ECSComponent oldComponent, bool silent, ECSEntity restoringOwner, bool restoringMode);

        /// <summary>Компонент помечен изменённым без замены значения: только dirty-set.
        /// Вызывается под write-локом ячейки.</summary>
        void ComponentMarkedChanged(long typeUid, ECSComponent component, bool serializationSilent);

        /// <summary>Removal-событие. Вызывается под write-локом ячейки; порядок примитива
        /// (внутри ExecuteOnRemoveLocked) — «коллбэк, ЗАТЕМ изъятие из словаря»: значение
        /// ещё видимо читателям без лока в момент вызова, но write-лок гарантирует, что
        /// локующие читатели его не возьмут.</summary>
        void ComponentRemoved(long typeUid, ECSComponent component);
    }

    /// <summary>
    /// Хранилище компонентов сущности: ТОЛЬКО хранение + транзакционная матрица +
    /// absence-holds. Пер-сущностный словарь: ключ — стабильный type-uid (long,
    /// == component.GetId() == type.TypeId()), HoldKeys ВКЛЮЧЁН — холды отсутствия
    /// (HoldComponentAddition / ExecuteOnNotHasComponent) — часть контрактной машинерии.
    ///
    /// Store не знает ни сущности, ни мира, ни сериализации, ни логгера: все side-effects —
    /// у слушателя.
    /// </summary>
    public sealed class ComponentStore : IEnumerable<KeyValuePair<long, ECSComponent>>
    {
        // Пер-сущностное хранилище — ComponentBag (компактный массив Cell[], лок инлайн
        // как long в ячейке, absence-holds — в том же слоте в состоянии ABSENT, без
        // вложенного словаря, слоты переиспользуются). Ключ type-uid — всегда в
        // int-диапазоне ([TypeUid(int)]), поэтому (int)typeUid — точная конверсия.
        private readonly ComponentBag<ECSComponent> _slots;
        private readonly IComponentStoreListener _listener;

        public ComponentStore(ConcurrencyMode mode, IComponentStoreListener listener)
        {
            if (listener == null) throw new ArgumentNullException("listener");
            _listener = listener;
            _slots = new ComponentBag<ECSComponent>(mode);
        }

        // ───────── транзакционная матрица (с нотификацией слушателя) ─────────

        /// <summary>Добавление: внешний ContainsKey-гейт + ExecuteOnAddLocked (двухфазная форма).</summary>
        public bool Add(long typeUid, ECSComponent component, bool restoringMode)
        {
            bool added = false;
            int k = (int)typeUid;
            if (!_slots.ContainsKey(k))
            {
                _slots.ExecuteOnAddLocked(k, component, (key, newcomponent) =>
                {
                    _listener.ComponentAdded(key, newcomponent, restoringMode);
                    added = true;
                });
            }
            return added;
        }

        /// <summary>Add-или-Change одним транзактом. changedBranch=true, если исполнилась
        /// change-ветка (исход dirty решает слушатель по silent). restoringOwner передаёт
        /// вызывающий (store сущности не знает): в restoring-режиме — сущность-владелец.</summary>
        public void AddOrChange(long typeUid, ECSComponent component, bool restoringMode, bool silent, ECSEntity restoringOwner, out bool added, out bool changedBranch)
        {
            bool a = false, c = false;
            _slots.ExecuteOnAddOrChangeLocked((int)typeUid, component, (key, newcomponent) =>
            {
                _listener.ComponentAdded(key, newcomponent, restoringMode);
                a = true;
            }, (key, newcomponent, oldcomponent) =>
            {
                _listener.ComponentChanged(key, newcomponent, oldcomponent, silent, restoringOwner, restoringMode);
                c = true;
            });
            added = a;
            changedBranch = c;
        }

        /// <summary>Замена значения.</summary>
        public bool Change(long typeUid, ECSComponent component, bool silent, ECSEntity restoringOwner)
        {
            bool changed = false;
            _slots.ExecuteOnChangeLocked((int)typeUid, component, (key, chcomponent, oldcomponent) =>
            {
                _listener.ComponentChanged(key, chcomponent, oldcomponent, silent, restoringOwner, false);
                changed = true;
            });
            return changed;
        }

        /// <summary>Пометка изменённым без замены значения.</summary>
        public bool MarkChanged(long typeUid, ECSComponent component, bool serializationSilent)
        {
            bool touched = false;
            _slots.ExecuteOnChangeLocked((int)typeUid, component, (key, chcomponent, oldcomponent) =>
            {
                _listener.ComponentMarkedChanged(key, chcomponent, serializationSilent);
                touched = true;
            });
            return touched;
        }

        /// <summary>Изъятие компонента из слота.</summary>
        public bool Remove(long typeUid, out ECSComponent component)
        {
            bool removed = false;
            _slots.ExecuteOnRemoveLocked((int)typeUid, out component, (key, victim) =>
            {
                _listener.ComponentRemoved(key, victim);
                removed = true;
            });
            return removed;
        }

        // ───────── absence-holds (контрактная машинерия) ─────────

        public bool ExecuteOnAbsent(long typeUid, Action action) { return _slots.ExecuteHoldRead((int)typeUid, action); }
        /// <summary>Holds по отсутствию всегда берут SHARED-lock (holdMode игнорируется,
        /// exclusive:false всегда).</summary>
        public bool HoldAbsence(long typeUid, out RWToken token, bool holdMode) { return _slots.Hold((int)typeUid, false, out token); }

        // ───────── проекции словаря (хранение, локи, lockdown/freeze) ─────────
        // ComponentBag покрывает ту же транзакционную матрицу через per-cell RWCell.

        public bool ContainsKey(long typeUid) { return _slots.ContainsKey((int)typeUid); }
        public bool TryGetValue(long typeUid, out ECSComponent component) { return _slots.TryGetValue((int)typeUid, out component); }
        /// <summary>Индексаторная семантика components[key]: KeyNotFoundException при отсутствии.</summary>
        public ECSComponent GetOrThrow(long typeUid)
        {
            ECSComponent component;
            if (_slots.TryGetValueUnsafe((int)typeUid, out component)) return component;
            throw new KeyNotFoundException();
        }

        // Прямой вызов action(typeUid, value) с исходным long — БЕЗ адаптер-замыкания int->long.
        public void ExecuteReadLocked(long typeUid, Action<long, ECSComponent> action)
        {
            ECSComponent v; RWToken t;
            if (_slots.TryGetReadLocked((int)typeUid, out v, out t)) { try { action(typeUid, v); } catch { } t.Dispose(); }
        }
        public void ExecuteWriteLocked(long typeUid, Action<long, ECSComponent> action)
        {
            ECSComponent v; RWToken t;
            if (_slots.TryGetWriteLocked((int)typeUid, out v, out t)) { try { action(typeUid, v); } catch { } t.Dispose(); }
        }
        public bool TryGetLockedElement(long typeUid, out ECSComponent component, out RWToken token, bool write) { return _slots.TryGetLocked((int)typeUid, write, out component, out token); }
        public RWToken LockStorage() { return _slots.LockStorage(); }
        public void EnterLockdown() { _slots.EnterLockdown(); }
        public void ExitLockdown() { _slots.ExitLockdown(); }

        public bool UnsafeAdd(long typeUid, ECSComponent component) { return _slots.UnsafeAdd((int)typeUid, component); }
        public bool UnsafeChange(long typeUid, ECSComponent component) { return _slots.UnsafeChange((int)typeUid, component); }
        public bool UnsafeRemove(long typeUid, out ECSComponent component) { return _slots.UnsafeRemove((int)typeUid, out component); }
        /// <summary>Прямое изъятие без слушателя (используется дословно сохранённым
        /// группоснятием RemoveComponentsWithGroup, где side-effects инлайновые).</summary>
        public bool RemoveRaw(long typeUid) { ECSComponent _; return _slots.Remove((int)typeUid, out _); }

        public ICollection<long> Keys
        {
            get
            {
                var snap = _slots.Snapshot();
                var list = new List<long>(snap.Count);
                for (int i = 0; i < snap.Count; i++) list.Add(snap[i].Key);
                return list;
            }
        }
        public ICollection<ECSComponent> Values
        {
            get
            {
                var snap = _slots.Snapshot();
                var list = new List<ECSComponent>(snap.Count);
                for (int i = 0; i < snap.Count; i++) list.Add(snap[i].Value);
                return list;
            }
        }
        public int Count { get { return _slots.Count; } }

        public IEnumerator<KeyValuePair<long, ECSComponent>> GetEnumerator()
        {
            var snap = _slots.Snapshot();
            for (int i = 0; i < snap.Count; i++)
                yield return new KeyValuePair<long, ECSComponent>(snap[i].Key, snap[i].Value);
        }
        IEnumerator IEnumerable.GetEnumerator() { return GetEnumerator(); }
    }


    public sealed class ComponentStoreLockSlim : IEnumerable<KeyValuePair<long, ECSComponent>>
    {
        private readonly LockedDictionarySlim<long, ECSComponent> _slots;
        private readonly IComponentStoreListener _listener;

        public ComponentStoreLockSlim(ConcurrencyMode mode, IComponentStoreListener listener)
        {
            if (listener == null) throw new ArgumentNullException("listener");
            _listener = listener;
            _slots = new LockedDictionarySlim<long, ECSComponent>(mode, preserveLockingKeys: true);
        }

        // ───────── транзакционная матрица (с нотификацией слушателя) ─────────

        /// <summary>Добавление (дословно прежняя двухфазная форма AddComponentImmediately:
        /// внешний ContainsKey-гейт + ExecuteOnAddLocked).</summary>
        public bool Add(long typeUid, ECSComponent component, bool restoringMode)
        {
            bool added = false;
            if (!_slots.ContainsKey(typeUid))
            {
                _slots.ExecuteOnAddLocked(typeUid, component, (key, newcomponent) =>
                {
                    _listener.ComponentAdded(key, newcomponent, restoringMode);
                    added = true;
                });
            }
            return added;
        }

        /// <summary>Add-или-Change одним транзактом (бывш. AddOrChangeComponentImmediately).
        /// changedBranch=true, если исполнилась change-ветка (исход dirty решает слушатель по silent).
        /// restoringOwner передаёт вызывающий (store сущности не знает): в restoring-режиме — сущность-владелец.</summary>
        public void AddOrChange(long typeUid, ECSComponent component, bool restoringMode, bool silent, ECSEntity restoringOwner, out bool added, out bool changedBranch)
        {
            bool a = false, c = false;
            _slots.ExecuteOnAddOrChangeLocked(typeUid, component, (key, newcomponent) =>
            {
                _listener.ComponentAdded(key, newcomponent, restoringMode);
                a = true;
            }, (key, newcomponent, oldcomponent) =>
            {
                _listener.ComponentChanged(key, newcomponent, oldcomponent, silent, restoringOwner, restoringMode);
                c = true;
            });
            added = a;
            changedBranch = c;
        }

        /// <summary>Замена значения (бывш. ChangeComponent).</summary>
        public bool Change(long typeUid, ECSComponent component, bool silent, ECSEntity restoringOwner)
        {
            bool changed = false;
            _slots.ExecuteOnChangeLocked(typeUid, component, (key, chcomponent, oldcomponent) =>
            {
                _listener.ComponentChanged(key, chcomponent, oldcomponent, silent, restoringOwner, false);
                changed = true;
            });
            return changed;
        }

        /// <summary>Пометка изменённым без замены значения (бывш. MarkComponentChanged).</summary>
        public bool MarkChanged(long typeUid, ECSComponent component, bool serializationSilent)
        {
            bool touched = false;
            _slots.ExecuteOnChangeLocked(typeUid, component, (key, chcomponent, oldcomponent) =>
            {
                _listener.ComponentMarkedChanged(key, chcomponent, serializationSilent);
                touched = true;
            });
            return touched;
        }

        /// <summary>Изъятие (бывш. Remove*Immediately ядро).</summary>
        public bool Remove(long typeUid, out ECSComponent component)
        {
            bool removed = false;
            _slots.ExecuteOnRemoveLocked(typeUid, out component, (key, victim) =>
            {
                _listener.ComponentRemoved(key, victim);
                removed = true;
            });
            return removed;
        }

        // ───────── absence-holds (контрактная машинерия, идея 1.13) ─────────

        public bool ExecuteOnAbsent(long typeUid, Action action) { return _slots.ExecuteOnKeyHolded(typeUid, action); }
        public bool HoldAbsence(long typeUid, out RWToken token, bool holdMode) { return _slots.HoldKey(typeUid, out token, holdMode); }

        // ───────── проекции словаря (хранение, локи, lockdown/freeze) ─────────
        // Имена и семантика — дословно LockedDictionarySlim: транзакционная матрица 9(а)
        // покрывает их через тот же примитив.

        public bool ContainsKey(long typeUid) { return _slots.ContainsKey(typeUid); }
        public bool TryGetValue(long typeUid, out ECSComponent component) { return _slots.TryGetValue(typeUid, out component); }
        /// <summary>Индексаторная семантика прежнего components[key]: KeyNotFoundException при отсутствии.</summary>
        public ECSComponent GetOrThrow(long typeUid) { return _slots[typeUid]; }

        public void ExecuteReadLocked(long typeUid, Action<long, ECSComponent> action) { _slots.ExecuteReadLocked(typeUid, action); }
        public void ExecuteWriteLocked(long typeUid, Action<long, ECSComponent> action) { _slots.ExecuteWriteLocked(typeUid, action); }
        public bool TryGetLockedElement(long typeUid, out ECSComponent component, out RWToken token, bool write) { return _slots.TryGetLockedElement(typeUid, out component, out token, write); }
        public RWToken LockStorage() { return _slots.LockStorage(); }
        public void EnterLockdown() { _slots.EnterLockdown(); }
        public void ExitLockdown() { _slots.ExitLockdown(); }

        public bool UnsafeAdd(long typeUid, ECSComponent component) { return _slots.UnsafeAdd(typeUid, component); }
        public bool UnsafeChange(long typeUid, ECSComponent component) { return _slots.UnsafeChange(typeUid, component); }
        public bool UnsafeRemove(long typeUid, out ECSComponent component) { return _slots.UnsafeRemove(typeUid, out component); }
        /// <summary>Прямое изъятие без слушателя (используется дословно сохранённым
        /// группоснятием RemoveComponentsWithGroup, где side-effects инлайновые).</summary>
        public bool RemoveRaw(long typeUid) { return _slots.Remove(typeUid); }

        public ICollection<long> Keys { get { return _slots.Keys; } }
        public ICollection<ECSComponent> Values { get { return _slots.Values; } }
        public int Count { get { return _slots.Count; } }

        public IEnumerator<KeyValuePair<long, ECSComponent>> GetEnumerator() { return _slots.GetEnumerator(); }
        IEnumerator IEnumerable.GetEnumerator() { return GetEnumerator(); }
    }
}
