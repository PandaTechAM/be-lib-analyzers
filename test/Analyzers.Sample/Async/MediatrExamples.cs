using System;
using System.Threading;
using System.Threading.Tasks;
using MediatR;

namespace Analyzers.Sample.Async;

public class MediatRequest : IRequest;

public class MediatRequestHandler : IRequestHandler<MediatRequest>
{
   // Contract implementation – should not be renamed to HandleAsync or forced CT renames.
   public Task Handle(MediatRequest request, CancellationToken cancellationToken)
   {
      throw new NotImplementedException();
   }
}