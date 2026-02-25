using Prism.Ioc;
using SmartInvoice.Application.Services;
using SmartInvoice.InvoicePdfFetchers;

namespace SmartInvoice.Bootstrapper;

/// <summary>
/// Cung cấp danh sách fetcher PDF đã đăng ký (Ehoadon, Skeleton, ...).
/// Dùng khi DI container (DryIoc) mặc định Replace khi đăng ký nhiều implementation cùng interface.
/// </summary>
public sealed class KeyedInvoicePdfFetcherProvider : IKeyedInvoicePdfFetcherProvider
{
    private readonly IContainerProvider _container;

    public KeyedInvoicePdfFetcherProvider(IContainerProvider container)
    {
        _container = container ?? throw new ArgumentNullException(nameof(container));
    }

    public IEnumerable<IKeyedInvoicePdfFetcher> GetFetchers()
    {
        return new IKeyedInvoicePdfFetcher[]
        {
            _container.Resolve<EhoadonInvoicePdfFetcher>(),
            _container.Resolve<HtInvoiceInvoicePdfFetcher>(),
            _container.Resolve<FastInvoicePdfFetcher>(),
            _container.Resolve<MeinvoiceInvoicePdfFetcher>(),
            _container.Resolve<EasyInvoicePdfFetcher>(),
            _container.Resolve<ViettelInvoicePdfFetcher>(),
            _container.Resolve<WinInvoicePdfFetcher>(),
            _container.Resolve<EinvoiceInvoicePdfFetcher>(),
            _container.Resolve<MerchantVnptInvoiceFetcher>(),
            _container.Resolve<InvoicePdfFetcherSkeleton>()
        };
    }
}
