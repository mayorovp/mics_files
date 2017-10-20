using Autofac;
using System;
using System.ServiceModel;
using System.ServiceModel.Activation;

namespace Helpers.ServiceModel
{
    public class AutofacServiceHostFactory : ServiceHostFactoryBase
    {
        public static ILifetimeScope Scope { get; set; }

        public override ServiceHostBase CreateServiceHost(string constructorString, Uri[] baseAddresses)
        {
            return new AutofacServiceHost(Scope, constructorString, baseAddresses);
        }
    }
}
