using AECC.Extensions;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace AECC.Core
{
    public partial class ECSEntity
    {
        #region Locked functions (Async)

        public Task ExecuteReadLockedComponentAsync(Type type, Func<Type, ECSComponent, Task> action)
        {
            return this.entityComponents.ExecuteReadLockedComponentAsync(type, action);
        }

        public Task ExecuteReadLockedComponentAsync<T>(Func<Type, ECSComponent, Task> action) where T : ECSComponent
        {
            return ExecuteReadLockedComponentAsync(typeof(T), action);
        }

        #region Generic_Extension (Async)

        public Task ExecuteWriteLockedComponentAsync<T1, T2, T3, T4, T5, T6>(Func<T1, T2, T3, T4, T5, T6, Task> action) 
            where T1 : ECSComponent where T2 : ECSComponent where T3 : ECSComponent 
            where T4 : ECSComponent where T5 : ECSComponent where T6 : ECSComponent
        {
            return ExecuteWriteLockedComponentAsync(typeof(T1), async (t, c1) =>
            {
                await ExecuteWriteLockedComponentAsync(typeof(T2), async (t2, c2) =>
                {
                    await ExecuteWriteLockedComponentAsync(typeof(T3), async (t3, c3) =>
                    {
                        await ExecuteWriteLockedComponentAsync(typeof(T4), async (t4, c4) =>
                        {
                            await ExecuteWriteLockedComponentAsync(typeof(T5), async (t5, c5) =>
                            {
                                await ExecuteWriteLockedComponentAsync(typeof(T6), async (t6, c6) => // Исправлена опечатка typeof(T5) из оригинала
                                {
                                    await action((T1)c1, (T2)c2, (T3)c3, (T4)c4, (T5)c5, (T6)c6);
                                });
                            });
                        });
                    });
                });
            });
        }

        public Task ExecuteWriteLockedComponentAsync<T1, T2, T3, T4, T5>(Func<T1, T2, T3, T4, T5, Task> action)
            where T1 : ECSComponent where T2 : ECSComponent where T3 : ECSComponent 
            where T4 : ECSComponent where T5 : ECSComponent
        {
            return ExecuteWriteLockedComponentAsync(typeof(T1), async (t1, c1) =>
            {
                await ExecuteWriteLockedComponentAsync(typeof(T2), async (t2, c2) =>
                {
                    await ExecuteWriteLockedComponentAsync(typeof(T3), async (t3, c3) =>
                    {
                        await ExecuteWriteLockedComponentAsync(typeof(T4), async (t4, c4) =>
                        {
                            await ExecuteWriteLockedComponentAsync(typeof(T5), async (t5, c5) =>
                            {
                                await action((T1)c1, (T2)c2, (T3)c3, (T4)c4, (T5)c5);
                            });
                        });
                    });
                });
            });
        }

        public Task ExecuteWriteLockedComponentAsync<T1, T2, T3, T4>(Func<T1, T2, T3, T4, Task> action)
            where T1 : ECSComponent where T2 : ECSComponent where T3 : ECSComponent where T4 : ECSComponent
        {
            return ExecuteWriteLockedComponentAsync(typeof(T1), async (t1, c1) =>
            {
                await ExecuteWriteLockedComponentAsync(typeof(T2), async (t2, c2) =>
                {
                    await ExecuteWriteLockedComponentAsync(typeof(T3), async (t3, c3) =>
                    {
                        await ExecuteWriteLockedComponentAsync(typeof(T4), async (t4, c4) =>
                        {
                            await action((T1)c1, (T2)c2, (T3)c3, (T4)c4);
                        });
                    });
                });
            });
        }

        public Task ExecuteWriteLockedComponentAsync<T1, T2, T3>(Func<T1, T2, T3, Task> action)
            where T1 : ECSComponent where T2 : ECSComponent where T3 : ECSComponent
        {
            return ExecuteWriteLockedComponentAsync(typeof(T1), async (t1, c1) =>
            {
                await ExecuteWriteLockedComponentAsync(typeof(T2), async (t2, c2) =>
                {
                    await ExecuteWriteLockedComponentAsync(typeof(T3), async (t3, c3) =>
                    {
                        await action((T1)c1, (T2)c2, (T3)c3);
                    });
                });
            });
        }
        
        public Task ExecuteWriteLockedComponentAsync<T1, T2>(Func<T1, T2, Task> action) 
            where T1 : ECSComponent where T2 : ECSComponent
        {
            return ExecuteWriteLockedComponentAsync(typeof(T1), async (t1, c1) =>
            {
                await ExecuteWriteLockedComponentAsync(typeof(T2), async (t2, c2) =>
                {
                    await action((T1)c1, (T2)c2);
                });
            });
        }

        public Task ExecuteWriteLockedComponentAsync<T1>(Func<T1, Task> action)
            where T1 : ECSComponent
        {
            return ExecuteWriteLockedComponentAsync(typeof(T1), async (t1, c1) =>
            {
                await action((T1)c1);
            });
        }

        public Task ExecuteReadLockedComponentAsync<T1, T2, T3, T4, T5, T6>(Func<T1, T2, T3, T4, T5, T6, Task> action) 
            where T1 : ECSComponent where T2 : ECSComponent where T3 : ECSComponent 
            where T4 : ECSComponent where T5 : ECSComponent where T6 : ECSComponent
        {
            return ExecuteReadLockedComponentAsync(typeof(T1), async (t1, c1) =>
            {
                await ExecuteReadLockedComponentAsync(typeof(T2), async (t2, c2) =>
                {
                    await ExecuteReadLockedComponentAsync(typeof(T3), async (t3, c3) =>
                    {
                        await ExecuteReadLockedComponentAsync(typeof(T4), async (t4, c4) =>
                        {
                            await ExecuteReadLockedComponentAsync(typeof(T5), async (t5, c5) =>
                            {
                                await ExecuteReadLockedComponentAsync(typeof(T6), async (t6, c6) =>
                                {
                                    await action((T1)c1, (T2)c2, (T3)c3, (T4)c4, (T5)c5, (T6)c6);
                                });
                            });
                        });
                    });
                });
            });
        }

        public Task ExecuteReadLockedComponentAsync<T1, T2, T3, T4, T5>(Func<T1, T2, T3, T4, T5, Task> action)
            where T1 : ECSComponent where T2 : ECSComponent where T3 : ECSComponent 
            where T4 : ECSComponent where T5 : ECSComponent
        {
            return ExecuteReadLockedComponentAsync(typeof(T1), async (t1, c1) =>
            {
                await ExecuteReadLockedComponentAsync(typeof(T2), async (t2, c2) =>
                {
                    await ExecuteReadLockedComponentAsync(typeof(T3), async (t3, c3) =>
                    {
                        await ExecuteReadLockedComponentAsync(typeof(T4), async (t4, c4) =>
                        {
                            await ExecuteReadLockedComponentAsync(typeof(T5), async (t5, c5) =>
                            {
                                await action((T1)c1, (T2)c2, (T3)c3, (T4)c4, (T5)c5);
                            });
                        });
                    });
                });
            });
        }

        public Task ExecuteReadLockedComponentAsync<T1, T2, T3, T4>(Func<T1, T2, T3, T4, Task> action)
            where T1 : ECSComponent where T2 : ECSComponent where T3 : ECSComponent where T4 : ECSComponent
        {
            return ExecuteReadLockedComponentAsync(typeof(T1), async (t1, c1) =>
            {
                await ExecuteReadLockedComponentAsync(typeof(T2), async (t2, c2) =>
                {
                    await ExecuteReadLockedComponentAsync(typeof(T3), async (t3, c3) =>
                    {
                        await ExecuteReadLockedComponentAsync(typeof(T4), async (t4, c4) =>
                        {
                            await action((T1)c1, (T2)c2, (T3)c3, (T4)c4);
                        });
                    });
                });
            });
        }

        public Task ExecuteReadLockedComponentAsync<T1, T2, T3>(Func<T1, T2, T3, Task> action)
            where T1 : ECSComponent where T2 : ECSComponent where T3 : ECSComponent
        {
            return ExecuteReadLockedComponentAsync(typeof(T1), async (t1, c1) =>
            {
                await ExecuteReadLockedComponentAsync(typeof(T2), async (t2, c2) =>
                {
                    await ExecuteReadLockedComponentAsync(typeof(T3), async (t3, c3) =>
                    {
                        await action((T1)c1, (T2)c2, (T3)c3);
                    });
                });
            });
        }
        
        public Task ExecuteReadLockedComponentAsync<T1, T2>(Func<T1, T2, Task> action) 
            where T1 : ECSComponent where T2 : ECSComponent
        {
            return ExecuteReadLockedComponentAsync(typeof(T1), async (t1, c1) =>
            {
                await ExecuteReadLockedComponentAsync(typeof(T2), async (t2, c2) =>
                {
                    await action((T1)c1, (T2)c2);
                });
            });
        }

        public Task ExecuteReadLockedComponentAsync<T1>(Func<T1, Task> action)
            where T1 : ECSComponent
        {
            return ExecuteReadLockedComponentAsync(typeof(T1), async (t1, c1) =>
            {
                await action((T1)c1);
            });
        }

        public Task ExecuteHoldComponentAsync<T1, T2, T3, T4, T5, T6>(Func<T1, T2, T3, T4, T5, T6, Task> action) 
            where T1 : ECSComponent where T2 : ECSComponent where T3 : ECSComponent 
            where T4 : ECSComponent where T5 : ECSComponent where T6 : ECSComponent
        {
            return this.entityComponents.ExecuteOnNotHasComponentAsync(typeof(T1), async () =>
            {
                await this.entityComponents.ExecuteOnNotHasComponentAsync(typeof(T2), async () =>
                {
                    await this.entityComponents.ExecuteOnNotHasComponentAsync(typeof(T3), async () =>
                    {
                        await this.entityComponents.ExecuteOnNotHasComponentAsync(typeof(T4), async () =>
                        {
                            await this.entityComponents.ExecuteOnNotHasComponentAsync(typeof(T5), async () =>
                            {
                                await this.entityComponents.ExecuteOnNotHasComponentAsync(typeof(T6), async () =>
                                {
                                    await action(default(T1), default(T2), default(T3), default(T4), default(T5), default(T6));
                                });
                            });
                        });
                    });
                });
            });
        }

        public Task ExecuteHoldComponentAsync<T1, T2, T3, T4, T5>(Func<T1, T2, T3, T4, T5, Task> action) 
            where T1 : ECSComponent where T2 : ECSComponent where T3 : ECSComponent 
            where T4 : ECSComponent where T5 : ECSComponent
        {
            return this.entityComponents.ExecuteOnNotHasComponentAsync(typeof(T1), async () =>
            {
                await this.entityComponents.ExecuteOnNotHasComponentAsync(typeof(T2), async () =>
                {
                    await this.entityComponents.ExecuteOnNotHasComponentAsync(typeof(T3), async () =>
                    {
                        await this.entityComponents.ExecuteOnNotHasComponentAsync(typeof(T4), async () =>
                        {
                            await this.entityComponents.ExecuteOnNotHasComponentAsync(typeof(T5), async () =>
                            {
                                await action(default(T1), default(T2), default(T3), default(T4), default(T5));
                            });
                        });
                    });
                });
            });
        }

        public Task ExecuteHoldComponentAsync<T1, T2, T3, T4>(Func<T1, T2, T3, T4, Task> action) 
            where T1 : ECSComponent where T2 : ECSComponent where T3 : ECSComponent where T4 : ECSComponent
        {
            return this.entityComponents.ExecuteOnNotHasComponentAsync(typeof(T1), async () =>
            {
                await this.entityComponents.ExecuteOnNotHasComponentAsync(typeof(T2), async () =>
                {
                    await this.entityComponents.ExecuteOnNotHasComponentAsync(typeof(T3), async () =>
                    {
                        await this.entityComponents.ExecuteOnNotHasComponentAsync(typeof(T4), async () =>
                        {
                            await action(default(T1), default(T2), default(T3), default(T4));
                        });
                    });
                });
            });
        }

        public Task ExecuteHoldComponentAsync<T1, T2, T3>(Func<T1, T2, T3, Task> action) 
            where T1 : ECSComponent where T2 : ECSComponent where T3 : ECSComponent
        {
            return this.entityComponents.ExecuteOnNotHasComponentAsync(typeof(T1), async () =>
            {
                await this.entityComponents.ExecuteOnNotHasComponentAsync(typeof(T2), async () =>
                {
                    await this.entityComponents.ExecuteOnNotHasComponentAsync(typeof(T3), async () =>
                    {
                        await action(default(T1), default(T2), default(T3));
                    });
                });
            });
        }

        public Task ExecuteHoldComponentAsync<T1, T2>(Func<T1, T2, Task> action) 
            where T1 : ECSComponent where T2 : ECSComponent
        {
            return this.entityComponents.ExecuteOnNotHasComponentAsync(typeof(T1), async () =>
            {
                await this.entityComponents.ExecuteOnNotHasComponentAsync(typeof(T2), async () =>
                {
                    await action(default(T1), default(T2));
                });
            });
        }

        public Task ExecuteHoldComponentAsync<T1>(Func<T1, Task> action)
            where T1 : ECSComponent
        {
            return this.entityComponents.ExecuteOnNotHasComponentAsync(typeof(T1), async () =>
            {
                await action(default(T1));
            });
        }
        
        #endregion

        public Task ExecuteWriteLockedComponentAsync(Type type, Func<Type, ECSComponent, Task> action)
        {
            return this.entityComponents.ExecuteWriteLockedComponentAsync(type, action);
        }

        public Task ExecuteWriteLockedComponentAsync<T>(Func<Type, ECSComponent, Task> action) where T : ECSComponent
        {
            return ExecuteWriteLockedComponentAsync(typeof(T), action);
        }
        #endregion

        #region BasedRealization (Async)

        private async Task AddComponentImplAsync(ECSComponent component, bool sendEvent)
        {
            await this.entityComponents.AddComponentImmediatelyAsync(component.GetTypeFast(), component, false, !sendEvent);
        }

        private async Task AddOrChangeComponentImplAsync(ECSComponent component, bool sendEvent, bool restoringOwner = false)
        {
            await this.entityComponents.AddOrChangeComponentImmediatelyAsync(component.GetTypeFast(), component, restoringOwner, !sendEvent);
        }

        public async Task<ECSComponent[]> GetComponentsAsync(params long[] componentTypeId)
        {
            List<ECSComponent> returnComponents = new List<ECSComponent>();
            foreach(var compId in componentTypeId)
            {
                try 
                { 
                    var comp = await this.entityComponents.GetComponentAsync(compId);
                    if (comp != null)
                        returnComponents.Add(comp); 
                } 
                catch { }
            }
            return returnComponents.ToArray();
        }

        #endregion

        #region Adapters (Async)

        public Task AddComponentAsync<T>() where T : ECSComponent, new()
        {
            return this.AddComponentAsync(typeof(T));
        }

        public Task AddComponentAsync(ECSComponent component)
        {
            return this.AddComponentImplAsync(component, true);
        }

        public Task<bool> TryAddComponentAsync(ECSComponent component)
        {
            return this.entityComponents.AddComponentImmediatelyAsync(component.GetTypeFast(), component);
        }

        public async Task AddComponentsAsync(IEnumerable<ECSComponent> components)
        {
            foreach(var component in components)
            {
                await this.AddComponentImplAsync(component, false);
            }
        }

        public async Task AddComponentsSilentAsync(IEnumerable<ECSComponent> components)
        {
            foreach (var component in components)
            {
                await this.AddComponentSilentAsync(component);
            }
        }

        public Task AddComponentAsync(Type componentType)
        {
            ECSComponent component = this.CreateNewComponentInstance(componentType);
            return this.AddComponentAsync(component);
        }

        public Task AddOrChangeComponentAsync(ECSComponent component)
        {
            return this.AddOrChangeComponentImplAsync(component, true);
        }

        public Task AddOrChangeComponentWithOwnerRestoringAsync(ECSComponent component)
        {
            return this.AddOrChangeComponentImplAsync(component, true, true);
        }

        public Task AddOrChangeComponentSilentWithOwnerRestoringAsync(ECSComponent component)
        {
            return this.AddOrChangeComponentImplAsync(component, false, true);
        }

        public Task AddOrChangeComponentSilentAsync(ECSComponent component)
        {
            return this.AddOrChangeComponentImplAsync(component, false);
        }
        
        public async Task<T> AddComponentAndGetInstanceAsync<T>() where T : ECSComponent, new()
        {
            ECSComponent component = this.CreateNewComponentInstance(typeof(T));
            await this.AddComponentAsync(component);
            return (T) component;
        }

        public Task AddComponentSilentAsync(ECSComponent component)
        {
            return this.AddComponentImplAsync(component, false);
        }

        public async Task ChangeComponentAsync(ECSComponent component)
        {
            bool flag = await this.HasComponentAsync(component.GetTypeFast()) && (await this.GetComponentAsync(component.GetTypeFast())).Equals(component);
            await this.entityComponents.ChangeComponentAsync(component);
        }

        public Task<bool> ChangeComponentSilentAsync(ECSComponent component)
        {
            return this.entityComponents.ChangeComponentAsync(component, true);
        }
        
        public async Task<T> GetComponentAsync<T>() where T : ECSComponent =>
            (T)await this.GetComponentAsync(typeof(T));

        public async Task<T> TryGetComponentAsync<T>() where T : ECSComponent
        {
            try { return (T)await this.GetComponentAsync(typeof(T)); } catch { return null; }
        }
            
        public Task<ECSComponent> GetComponentAsync(Type componentType) =>
            this.entityComponents.GetComponentAsync(componentType);

        public Task<ECSComponent> GetComponentAsync(long componentTypeId) =>
            this.entityComponents.GetComponentAsync(componentTypeId);

        public async Task<T> GetComponentAsync<T>(long componentTypeId) where T : ECSComponent =>
            (T)await this.entityComponents.GetComponentAsync(componentTypeId);

        public Task<ECSComponent> GetComponentUnsafeAsync(Type componentType) =>
            this.entityComponents.GetComponentUnsafeAsync(componentType);

        public Task<ECSComponent> GetComponentUnsafeAsync(long componentTypeId) =>
            this.entityComponents.GetComponentUnsafeAsync(componentTypeId);

        public Task<bool> HasComponentAsync<T>() where T : ECSComponent =>
            this.HasComponentAsync(typeof(T));

        public Task<bool> HasComponentAsync(Type type) =>
            this.entityComponents.HasComponentAsync(type);

        public Task<bool> HasComponentAsync(long componentClassId) =>
            this.entityComponents.HasComponentAsync(componentClassId);

        public async Task<bool> IsSameGroupAsync<T>(ECSEntity otherEntity) where T : ECSComponentGroup =>
            (await this.HasComponentAsync<T>() && await otherEntity.HasComponentAsync<T>()) && (await this.GetComponentAsync<T>()).GetId().Equals((await otherEntity.GetComponentAsync<T>()).GetId());

        public async Task OnDeleteAsync()
        {
            this.Alive = false;
            this.dataAccessPolicies.Clear();
            await this.entityComponents.OnEntityDeleteAsync();
            this.fastEntityComponentsId.ClearI(this.SerialLocker);
        }

        public Task RemoveComponentAsync<T>() where T : ECSComponent
        {
            return this.RemoveComponentAsync(typeof(T));
        }

        public Task RemoveComponentAsync(Type componentType)
        {
            return this.entityComponents.RemoveComponentImmediatelyAsync(componentType);
        }

        public Task RemoveComponentsWithGroupAsync(ECSComponentGroup componentGroup)
        {
            return this.entityComponents.RemoveComponentsWithGroupAsync(componentGroup.GetId());
        }

        public Task RemoveComponentsWithGroupAsync(long componentGroup)
        {
            return this.entityComponents.RemoveComponentsWithGroupAsync(componentGroup);
        }

        public Task RemoveComponentAsync(long componentTypeId)
        {
            return this.entityComponents.RemoveComponentImmediatelyAsync(componentTypeId);
        }

        public async Task<bool> TryRemoveComponentAsync(long componentTypeId)
        {
            return (await this.entityComponents.RemoveComponentImmediatelyAsync(componentTypeId)) != null;
        }

        public async Task RemoveComponentIfPresentAsync<T>() where T : ECSComponent
        {
            if (await this.HasComponentAsync<T>())
            {
                await this.RemoveComponentAsync(typeof(T));
            }
        }

        #endregion
    }
}