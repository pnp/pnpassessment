using FluentAssertions;
using PnP.Scanning.Core.Services;
using Xunit;

namespace PnP.Scanning.Core.Tests.Services
{
    /// <summary>
    /// T17 — verifies the retirement gating decision exposed by
    /// <see cref="AssessmentAvailability"/>. Pure predicates only (no CLI host): the
    /// <c>StartCommandHandler</c> guards call into these, and the default "all classic components"
    /// set is derived by filtering on <see cref="AssessmentAvailability.IsRetired(ClassicComponent)"/>.
    /// </summary>
    public class AssessmentAvailabilityTests
    {
        [Fact]
        public void Availability_WorkflowMode_IsRetired()
        {
            AssessmentAvailability.IsRetired(Mode.Workflow).Should().BeTrue();
        }

        [Fact]
        public void Availability_NonWorkflowModes_NotRetired()
        {
            // Mode is internal, so it cannot appear in a public [Theory] signature (CS0051) —
            // iterate the non-Workflow modes inside the fact instead (same idiom as ClassicOptionsTests).
            var nonWorkflowModes = Enum.GetValues(typeof(Mode)).Cast<Mode>().Where(m => m != Mode.Workflow);

            foreach (var mode in nonWorkflowModes)
            {
                AssessmentAvailability.IsRetired(mode).Should().BeFalse($"{mode} is not retired");
            }
        }

        [Fact]
        public void Availability_WorkflowClassicComponent_IsRetired()
        {
            AssessmentAvailability.IsRetired(ClassicComponent.Workflow).Should().BeTrue();
        }

        [Fact]
        public void Availability_NonWorkflowClassicComponents_NotRetired()
        {
            // ClassicComponent is internal — iterate inside the fact rather than via [Theory] (CS0051).
            var nonWorkflowComponents = Enum.GetValues(typeof(ClassicComponent)).Cast<ClassicComponent>()
                .Where(c => c != ClassicComponent.Workflow);

            foreach (var component in nonWorkflowComponents)
            {
                AssessmentAvailability.IsRetired(component).Should().BeFalse($"{component} is not retired");
            }
        }

        [Fact]
        public void Availability_DefaultClassicComponentSet_ExcludesWorkflow()
        {
            // This is exactly how StartCommandHandler builds the default "all components" set.
            var defaultSet = Enum.GetValues(typeof(ClassicComponent)).Cast<ClassicComponent>()
                .Where(c => !AssessmentAvailability.IsRetired(c))
                .ToList();

            defaultSet.Should().NotContain(ClassicComponent.Workflow);
            defaultSet.Should().Contain(new[]
            {
                ClassicComponent.Pages,
                ClassicComponent.Lists,
                ClassicComponent.Extensibility,
                ClassicComponent.InfoPath,
            });
        }
    }
}
