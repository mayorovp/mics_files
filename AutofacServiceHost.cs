using Autofac;
using System;
using System.Collections.Generic;
using System.Linq;
using System.ServiceModel;
using System.ServiceModel.Description;
using System.Collections.ObjectModel;
using System.ServiceModel.Channels;
using System.ServiceModel.Dispatcher;
using System.Diagnostics.Contracts;
using System.Reflection;
using Autofac.Core;
using System.Threading;

namespace Helpers.ServiceModel
{
    public class AutofacServiceHost : ServiceHostBase, IServiceBehavior
    {
        private readonly ILifetimeScope scope;
        private readonly string configurationName;

        public AutofacServiceHost(ILifetimeScope scope, string configurationName, params Uri[] baseAddresses)
        {
            Contract.Requires<ArgumentNullException>(scope != null);
            Contract.Requires<ArgumentNullException>(configurationName != null);
            Contract.Requires<ArgumentNullException>(baseAddresses != null);

            this.scope = scope;
            this.configurationName = configurationName;
            InitializeDescription(new UriSchemeKeyedCollection(baseAddresses));
        }

        protected override ServiceDescription CreateDescription(out IDictionary<string, ContractDescription> implementedContracts)
        {
            var contracts = from registration in scope.ComponentRegistry.Registrations
                            from service in registration.Services.OfType<IServiceWithType>()
                            let type = service.ServiceType
                            where type.GetCustomAttribute<ServiceContractAttribute>() != null
                            select ContractDescription.GetContract(type);

            implementedContracts = contracts.ToDictionary(contract => contract.ConfigurationName);
            return new ServiceDescription { ConfigurationName = configurationName, Behaviors = { this } };
        }

        void IServiceBehavior.Validate(ServiceDescription serviceDescription, ServiceHostBase serviceHostBase) { }

        void IServiceBehavior.AddBindingParameters(ServiceDescription serviceDescription, ServiceHostBase serviceHostBase, Collection<ServiceEndpoint> endpoints, BindingParameterCollection bindingParameters) { }

        void IServiceBehavior.ApplyDispatchBehavior(ServiceDescription serviceDescription, ServiceHostBase serviceHostBase)
        {
            var q = from channelDispatcher in ChannelDispatchers.OfType<ChannelDispatcher>()
                    from endpointDispatcher in channelDispatcher.Endpoints
                    where !endpointDispatcher.IsSystemEndpoint
                    join endpoint in serviceDescription.Endpoints 
                        on Tuple.Create(endpointDispatcher.EndpointAddress, endpointDispatcher.ContractName, endpointDispatcher.ContractNamespace) 
                        equals Tuple.Create(endpoint.Address, endpoint.Contract.Name, endpoint.Contract.Namespace)
                    group new { endpointDispatcher, endpoint } by endpoint.Contract into contract
                    group contract by scope.ComponentRegistry.RegistrationsFor(new TypedService(contract.Key.ContractType)).Single();

            foreach (var registration in q)
            {
                ServiceBehaviorAttribute behavior;
                object behaviorObj;
                if (registration.Key.Metadata.TryGetValue("behavior", out behaviorObj))
                    behavior = (ServiceBehaviorAttribute)behaviorObj;
                else
                    behavior = registration.Key.Activator.LimitType.GetCustomAttribute<ServiceBehaviorAttribute>();
                if (behavior == null)
                    behavior = new ServiceBehaviorAttribute();

                IInstanceContextProvider provider;
                switch (behavior.InstanceContextMode)
                {
                    case InstanceContextMode.Single: provider = new SingletonInstanceContextProvider(this, scope, registration.Key); break;
                    case InstanceContextMode.PerSession: provider = new PerSessionInstanceContextProvider(this, scope); break;
                    case InstanceContextMode.PerCall: provider = new PerCallInstanceContextProvider(this, scope); break;
                    default: throw new InvalidOperationException();
                }

                foreach (var contract in registration)
                {
                    foreach (var b in contract.Key.Operations.SelectMany(op => op.OperationBehaviors).OfType<DataContractSerializerOperationBehavior>())
                    {
                        b.IgnoreExtensionDataObject = behavior.IgnoreExtensionDataObject;
                        b.MaxItemsInObjectGraph = behavior.MaxItemsInObjectGraph;
                    }

                    foreach (var x in contract)
                    {
                        var runtime = x.endpointDispatcher.DispatchRuntime;

                        runtime.ConcurrencyMode = behavior.ConcurrencyMode;
                        runtime.EnsureOrderedDispatch = behavior.EnsureOrderedDispatch;
                        runtime.ValidateMustUnderstand = behavior.ValidateMustUnderstand;
                        runtime.AutomaticInputSessionShutdown = behavior.AutomaticSessionShutdown;
                        runtime.TransactionAutoCompleteOnSessionClose = behavior.TransactionAutoCompleteOnSessionClose;
                        runtime.ReleaseServiceInstanceOnTransactionComplete = behavior.ReleaseServiceInstanceOnTransactionComplete;

                        if (!behavior.UseSynchronizationContext) runtime.SynchronizationContext = null;

                        var address = x.endpointDispatcher.EndpointAddress;
                        switch (behavior.AddressFilterMode)
                        {
                            case AddressFilterMode.Any: x.endpointDispatcher.AddressFilter = new MatchAllMessageFilter(); break;
                            case AddressFilterMode.Prefix: x.endpointDispatcher.AddressFilter = new PrefixEndpointAddressMessageFilter(x.endpointDispatcher.EndpointAddress); break;
                            case AddressFilterMode.Exact: x.endpointDispatcher.AddressFilter = new EndpointAddressMessageFilter(x.endpointDispatcher.EndpointAddress); break;
                        }

                        Console.WriteLine("Channel: {0}, Endpoint: {1}, Contract: {2}, Callback: {3}, Registration: {4}",
                            x.endpointDispatcher.ChannelDispatcher.BindingName,
                            x.endpointDispatcher.EndpointAddress,
                            contract.Key.ConfigurationName,
                            contract.Key.CallbackContractType?.Name,
                            registration.Key.Activator.LimitType.Name
                        );

                        runtime.InstanceProvider = new InstanceProvider(contract.Key.CallbackContractType, registration.Key);
                        runtime.InstanceContextProvider = provider;
                    }
                }
            }
        }
        
        private class SingletonInstanceContextProvider : IInstanceContextProvider
        {
            private readonly ILifetimeScope scope;
            private readonly ServiceHostBase host;
            private readonly IComponentRegistration registration;

            private bool initialized;
            private object syncLock;
            private InstanceContext singletonContext;

            public SingletonInstanceContextProvider(ServiceHostBase host, ILifetimeScope scope, IComponentRegistration registration)
            {
                this.host = host;
                this.scope = scope;
                this.registration = registration;
            }

            public InstanceContext GetExistingInstanceContext(Message message, IContextChannel channel)
            {
                return LazyInitializer.EnsureInitialized(ref singletonContext, ref initialized, ref syncLock, () =>
                {
                    var instance = scope.ResolveComponent(registration, Enumerable.Empty<Parameter>());

                    var instanceContext = new InstanceContext(host, instance);
                    instanceContext.Extensions.Add(new InstanceContextExtension(instanceContext, null, null));
                    return instanceContext;
                });
            }

            public void InitializeInstanceContext(InstanceContext instanceContext, Message message, IContextChannel channel) { }

            public bool IsIdle(InstanceContext instanceContext)
                => instanceContext.State != CommunicationState.Opened;

            public void NotifyIdle(InstanceContextIdleCallback callback, InstanceContext instanceContext)
                => instanceContext.Extensions.Find<InstanceContextExtension>().OnIdle += callback;
        }

        private class PerSessionInstanceContextProvider : IInstanceContextProvider
        {
            private readonly ILifetimeScope scope;
            private readonly ServiceHostBase host;

            public PerSessionInstanceContextProvider(ServiceHostBase host, ILifetimeScope scope)
            {
                this.host = host;
                this.scope = scope;
            }

            public InstanceContext GetExistingInstanceContext(Message message, IContextChannel channel)
                =>  channel.Extensions.Find<InstanceContextExtension>()?.instanceContext;

            public void InitializeInstanceContext(InstanceContext instanceContext, Message message, IContextChannel channel)
            {
                new InstanceContextExtension(instanceContext, scope.BeginLifetimeScope(), channel);
            }

            public bool IsIdle(InstanceContext instanceContext)
                => instanceContext.Extensions.Find<InstanceContextExtension>().channel.State != CommunicationState.Opened;

            public void NotifyIdle(InstanceContextIdleCallback callback, InstanceContext instanceContext)
                => instanceContext.Extensions.Find<InstanceContextExtension>().OnIdle += callback;
        }

        private class PerCallInstanceContextProvider : IInstanceContextProvider
        {
            private readonly ILifetimeScope scope;
            private readonly ServiceHostBase host;

            public PerCallInstanceContextProvider(ServiceHostBase host, ILifetimeScope scope)
            {
                this.host = host;
                this.scope = scope;
            }

            public InstanceContext GetExistingInstanceContext(Message message, IContextChannel channel)
                => null;

            public void InitializeInstanceContext(InstanceContext instanceContext, Message message, IContextChannel channel)
            {
                new InstanceContextExtension(instanceContext, scope.BeginLifetimeScope(), channel);
            }

            public bool IsIdle(InstanceContext instanceContext)
                => true;

            public void NotifyIdle(InstanceContextIdleCallback callback, InstanceContext instanceContext) { }
        }

        private class InstanceContextExtension : IExtension<IContextChannel>, IExtension<InstanceContext>
        {
            public readonly InstanceContext instanceContext;
            public readonly ILifetimeScope lifetimeScope;
            public readonly IContextChannel channel;

            public InstanceContextExtension(InstanceContext instanceContext, ILifetimeScope lifetimeScope, IContextChannel channel)
            {
                this.instanceContext = instanceContext;
                this.lifetimeScope = lifetimeScope;
                this.channel = channel;

                instanceContext?.Extensions?.Add(this);
                channel?.Extensions?.Add(this);
            }

            public event InstanceContextIdleCallback OnIdle;

            void IExtension<InstanceContext>.Attach(InstanceContext owner)
            {
                owner.Closed += Context_Closed;
                owner.Faulted += Context_Closed;
            }

            private void Context_Closed(object sender, EventArgs e)
            {
                lifetimeScope?.Dispose();
            }

            void IExtension<IContextChannel>.Attach(IContextChannel owner)
            {
                owner.Closed += Session_Closed;
                owner.Faulted += Session_Closed;
            }

            private void Session_Closed(object sender, EventArgs e)
            {
                OnIdle?.Invoke(instanceContext);
            }

            void IExtension<InstanceContext>.Detach(InstanceContext owner) { throw new InvalidOperationException(); }
            void IExtension<IContextChannel>.Detach(IContextChannel owner) { throw new InvalidOperationException(); }
        }

        private class InstanceProvider : IInstanceProvider
        {
            private readonly Type callbackContractType;
            private readonly IComponentRegistration registration;

            public InstanceProvider(Type callbackContractType, IComponentRegistration registration)
            {
                this.callbackContractType = callbackContractType;
                this.registration = registration;
            }

            public object GetInstance(InstanceContext instanceContext)
            {
                var ext = instanceContext.Extensions.Find<InstanceContextExtension>();

                if (callbackContractType == null)
                    return ext.lifetimeScope.ResolveComponent(registration, Enumerable.Empty<Parameter>());
                else
                    return ext.lifetimeScope.ResolveComponent(registration, new Parameter[] { new TypedParameter(callbackContractType, ext.channel) });
            }

            public object GetInstance(InstanceContext instanceContext, Message message)
                => GetInstance(instanceContext);

            public void ReleaseInstance(InstanceContext instanceContext, object instance)
            {
            }
        }
    }
}
