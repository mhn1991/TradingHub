namespace DBManager;

using Npgsql;
using System.Data;

public interface IDbConnectionFactory
{
    IDbConnection CreateConnection();
}