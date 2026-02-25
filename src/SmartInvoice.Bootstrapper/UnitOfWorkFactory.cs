using Prism.Ioc;
using SmartInvoice.Application.Services;
using SmartInvoice.Core.Repositories;

namespace SmartInvoice.Bootstrapper;

/// <summary>Tạo UnitOfWork mới mỗi lần từ container (worker nền dùng DbContext riêng, thread-safe).</summary>
public sealed class UnitOfWorkFactory : IUnitOfWorkFactory
{
    private readonly IContainerProvider _container;

    public UnitOfWorkFactory(IContainerProvider container)
    {
        _container = container;
    }

    public IUnitOfWork Create() => _container.Resolve<IUnitOfWork>();
}
