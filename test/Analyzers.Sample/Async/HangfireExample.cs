using System.Threading;
using Hangfire;
using MediatR;

namespace Analyzers.Sample.Async;

public static class HangfireExample
{
   public static void ScheduleJobs()
   {
      RecurringJob.AddOrUpdate<ISender>(
         "Create Debts From Configs",
         sender => sender.Send(new MediatRequest(), CancellationToken.None),
         Cron.Monthly(1, 0, 1));
   }
}