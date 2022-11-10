using HyperMapper.Resolvers;
using HyperMapper.TestsCore.Data;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HyperMapper.TestsCore;

[TestClass]
public class MappingTests
{
    [TestMethod]
    public void MappingBaseClassPrivateSetter_valid()
    {
        var from = new DataFrom { Id = 1 };

        var mapper = StandardResolver.Default.GetMapper<DataFrom, DataTo>();

        var to = mapper.Map(from, StandardResolver.Default);

        Assert.IsTrue(to.Id == from.Id);        
    }
}