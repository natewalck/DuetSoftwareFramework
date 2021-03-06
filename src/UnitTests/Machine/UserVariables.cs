﻿using DuetAPI.Machine;
using NUnit.Framework;

namespace DuetUnitTest.Machine
{
    [TestFixture]
    public class UserVariables
    {
        [Test]
        public void Clone()
        {
            MachineModel original = new MachineModel();
            original.UserVariables.Add(new UserVariable { Name = "Variable 1", Value = "Value 1" });
            original.UserVariables.Add(new UserVariable { Name = "Variable 2", Value = "Value 2" });

            MachineModel clone = (MachineModel)original.Clone();

            Assert.AreEqual(original.UserVariables.Count, clone.UserVariables.Count);
            Assert.AreEqual(original.UserVariables[0].Name, clone.UserVariables[0].Name);
        }

        [Test]
        public void Assign()
        {
            MachineModel original = new MachineModel();
            original.UserVariables.Add(new UserVariable { Name = "Variable 1", Value = "Value 1" });
            original.UserVariables.Add(new UserVariable { Name = "Variable 2", Value = "Value 2" });

            MachineModel assigned = new MachineModel();
            assigned.Assign(original);

            Assert.AreEqual(original.UserVariables.Count, assigned.UserVariables.Count);
            Assert.AreEqual(original.UserVariables[0].Name, assigned.UserVariables[0].Name);
        }
    }
}
