using System.Threading;
using System.Threading.Tasks;
using MediatR;

namespace Analyzers.Sample.Async;

public class MediatRequest : IRequest;

public class MediatRequestHandler : IRequestHandler<MediatRequest>
{
   // This method should not be renamed to HandleAsync
   
   public Task Handle(MediatRequest request, CancellationToken ct)
   {
      throw new System.NotImplementedException();
   }
}