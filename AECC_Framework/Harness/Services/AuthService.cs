using AECC.Core;
using AECC.Core.Logging;
using AECC.ECS.DefaultObjects.ECSComponents;
using AECC.ECS.DefaultObjects.Events.ECSEvents;
using AECC.ECS.DefaultObjects.Events.LowLevelNetEvent.Auth;
using AECC.Extensions;
using AECC.Harness.Model;
using AECC.Network;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AECC.Harness.Services
{
    public
#if GODOT4_0_OR_GREATER
    partial
#endif
    class AuthService : IService
    {
        public Func<UserDataRowBase, ECSEntity> AuthorizationRealization = null;
        public Func<ClientRegistrationEvent, UserDataRowBase> SetupAuthorizationRealization = null;
        private static AuthService cacheInstance;
        #region client
        public string LastSendedUsername;
        public string LastSendedPassword;
        #endregion
        public static AuthService instance
        {
            get
            {
                if (cacheInstance == null)
                    cacheInstance = SGT.Get<AuthService>();
                return cacheInstance;
            }
        }
        public ConcurrentDictionary<ISocketAdapter, ECSEntity> SocketToEntity = new ConcurrentDictionary<ISocketAdapter, ECSEntity>();
        public ConcurrentDictionary<ECSEntity, ISocketAdapter> EntityToSocket = new ConcurrentDictionary<ECSEntity, ISocketAdapter>();

        public void AuthProcess(ClientAuthEvent clientAuthEvent)
        {
            if(DBService.instance.DBProvider.LoginCheck(clientAuthEvent.Username, HashExtension.MD5(clientAuthEvent.Password)))
            {
                var userdata = DBService.instance.DBProvider.GetUserViaCallsign<UserDataRowBase>(clientAuthEvent.Username);
                if(AuthorizationRealization != null)
                {
                    AuthorizationProcess(userdata, clientAuthEvent.SocketSource);
                }
                else
                {
                    NLogger.Error("Not initialized AuthService.instance.AuthorizationRealization method");
                }
            }
            else
            {
                NetworkService.instance.EventManager.Dispatch(new AuthActionFailedEvent()
                {
                    Reason = "Wrong username or password",
                    Destination = clientAuthEvent.Destination
                });
            }
        }

        public void RegistrationProcess(ClientRegistrationEvent clientAuthEvent)
        {
            if (DBService.instance.DBProvider.UsernameAvailable(clientAuthEvent.Username) &&
                DBService.instance.DBProvider.EmailAvailable(clientAuthEvent.Email))
            {
                if (SetupAuthorizationRealization != null)
                {
                    var userdata = DBService.instance.DBProvider.CreateUser<UserDataRowBase>(SetupAuthorizationRealization(clientAuthEvent));
                    if (AuthorizationRealization != null)
                    {
                        AuthorizationProcess(userdata, clientAuthEvent.SocketSource);
                    }
                    else
                    {
                        NLogger.Error("Not initialized AuthService.instance.AuthorizationRealization method");
                    }
                }
                else
                {
                    NLogger.Error("Not initialized AuthService.instance.SetupAuthorizationRealization method");
                }
            }
        }

        private void AuthorizationProcess(UserDataRowBase userData, ISocketAdapter socketAdapter)
        {
            var entity = SocketToEntity.Values.Where(x => x.GetComponent<UsernameComponent>().Username == userData.Username).FirstOrDefault();
            var userLogged = new UserLoggedEvent();
            if(entity == null)
            {
                entity = AuthorizationRealization(userData);
                entity.AddComponentSilent(new SocketComponent() { Socket = socketAdapter  });
                SocketToEntity[socketAdapter] = entity;
                EntityToSocket[entity] = socketAdapter;
                if (entity.ECSWorldOwner == null)
                {
                    NLogger.Error("entity.ECSWorldOwner == null");
                    return;
                }
                entity.ECSWorldOwner.entityManager.AddNewEntity(entity);
                userLogged.userRelogin = false;
            }
            else
            {
                var oldsocket = entity.GetComponent<SocketComponent>().Socket;
                SocketToEntity[socketAdapter] = entity;
                EntityToSocket[entity] = socketAdapter;
                if(oldsocket != socketAdapter)
                {
                    SocketToEntity.Remove(oldsocket, out _);
                }
                entity.GetComponent<SocketComponent>().Socket = socketAdapter;
                userLogged.userRelogin = true;
            }
            userLogged.Username = userData.Username;
            userLogged.userEntity = entity;
            userLogged.userEntityId = entity.instanceId;
            userLogged.Destination = socketAdapter.CachedDestination;
            NetworkService.instance.EventManager.Dispatch(userLogged);
        }

        public override void InitializeProcess()
        {
            
        }

        public override void OnDestroyReaction()
        {
            
        }

        public override void PostInitializeProcess()
        {
            
        }

        protected override Action<int>[] GetInitializationSteps()
        {
            return new Action<int>[]
            {
                (step) => {  },
            };
        }

        protected override void SetupCallbacks(List<IService> allServices)
        {
            
        }
    }
}
