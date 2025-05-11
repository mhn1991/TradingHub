using Microsoft.EntityFrameworkCore;
using System;
using DBManager.Data;

namespace DBManager
{
    public class DbConnectionChecker
    {
        private readonly AppDbContext _context;

        public DbConnectionChecker(AppDbContext context)
        {
            _context = context;
        }

        public bool CheckConnection()
        {
            try
            {
                // Check if the database is reachable
                return _context.Database.CanConnect();
            }
            catch (Exception ex)
            {
                // Log or handle the error
                Console.WriteLine($"Error: {ex.Message}");
                return false;
            }
        }
    }
}