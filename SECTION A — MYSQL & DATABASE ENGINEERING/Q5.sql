-- MySQL 8.0+. Optional values are bound parameters. Only allowlisted sort
-- expressions are added to the SQL text.

CREATE TABLE Customers (
    Id INT PRIMARY KEY,
    Name VARCHAR(100) NOT NULL,
    Country CHAR(2) NOT NULL,
    City VARCHAR(50) NOT NULL
);

CREATE TABLE Orders (
    Id INT PRIMARY KEY AUTO_INCREMENT,
    CustomerId INT NOT NULL,
    OrderDate DATETIME NOT NULL,
    Amount DECIMAL(12,2) NOT NULL,
    Status VARCHAR(20) NOT NULL,
    FOREIGN KEY (CustomerId) REFERENCES Customers(Id)
);

CREATE INDEX idx_orders_date_status ON Orders(OrderDate, Status, Amount);
CREATE INDEX idx_customers_country_city_name ON Customers(Country, City, Name);

DELIMITER $$
CREATE PROCEDURE SearchOrders(
    IN p_name VARCHAR(100),
    IN p_from DATE,
    IN p_to DATE,
    IN p_min DECIMAL(12,2),
    IN p_max DECIMAL(12,2),
    IN p_status VARCHAR(20),
    IN p_country CHAR(2),
    IN p_city VARCHAR(50),
    IN p_sortby VARCHAR(20),
    IN p_dir VARCHAR(4),
    IN p_page INT,
    IN p_size INT
)
BEGIN
    DECLARE v_sort VARCHAR(30);
    DECLARE v_dir VARCHAR(4);
    DECLARE v_where TEXT;
    DECLARE v_page INT DEFAULT 1;
    DECLARE v_size INT DEFAULT 20;
    DECLARE v_offset BIGINT UNSIGNED;

    SET v_sort = CASE COALESCE(p_sortby, 'OrderDate')
        WHEN 'CustomerName' THEN 'c.Name'
        WHEN 'Amount' THEN 'o.Amount'
        WHEN 'Status' THEN 'o.Status'
        WHEN 'OrderDate' THEN 'o.OrderDate'
        ELSE NULL
    END;
    IF v_sort IS NULL THEN
        SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Invalid SortBy value';
    END IF;

    SET v_dir = UPPER(COALESCE(p_dir, 'DESC'));
    IF v_dir NOT IN ('ASC', 'DESC') THEN
        SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Sort direction must be ASC or DESC';
    END IF;

    SET v_page = COALESCE(p_page, 1);
    SET v_size = COALESCE(p_size, 20);
    IF v_page < 1 OR v_size < 1 OR v_size > 100 THEN
        SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Page must be positive and PageSize must be 1 to 100';
    END IF;
    IF p_from IS NOT NULL AND p_to IS NOT NULL AND p_from > p_to THEN
        SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'DateFrom must not be later than DateTo';
    END IF;
    IF p_min IS NOT NULL AND p_max IS NOT NULL AND p_min > p_max THEN
        SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'MinAmount must not be greater than MaxAmount';
    END IF;

    SET v_offset = (CAST(v_page AS UNSIGNED) - 1) * CAST(v_size AS UNSIGNED);

    -- NULL means the filter was not supplied. The end date is exclusive so
    -- the whole DateTo calendar day is included without applying DATE() to a column.
    SET @search_name = IF(p_name IS NULL, NULL, CONCAT(p_name, '%'));
    SET @search_from = p_from;
    SET @search_to_exclusive = DATE_ADD(p_to, INTERVAL 1 DAY);
    SET @search_min = p_min;
    SET @search_max = p_max;
    SET @search_status = p_status;
    SET @search_country = p_country;
    SET @search_city = p_city;
    SET @search_limit = v_size;
    SET @search_offset = v_offset;

    -- Keep one placeholder per filter, even when that filter is absent. This
    -- keeps the EXECUTE USING parameter list the same for every search.
    SET v_where = CONCAT(
        ' WHERE 1=1',
        IF(@search_name IS NULL, ' AND ? IS NULL', ' AND c.Name LIKE ?'),
        IF(@search_from IS NULL, ' AND ? IS NULL', ' AND o.OrderDate >= ?'),
        IF(@search_to_exclusive IS NULL, ' AND ? IS NULL', ' AND o.OrderDate < ?'),
        IF(@search_min IS NULL, ' AND ? IS NULL', ' AND o.Amount >= ?'),
        IF(@search_max IS NULL, ' AND ? IS NULL', ' AND o.Amount <= ?'),
        IF(@search_status IS NULL, ' AND ? IS NULL', ' AND o.Status = ?'),
        IF(@search_country IS NULL, ' AND ? IS NULL', ' AND c.Country = ?'),
        IF(@search_city IS NULL, ' AND ? IS NULL', ' AND c.City = ?')
    );

    -- The count and page use exactly the same filters.
    SET @search_sql = CONCAT(
        'SELECT COUNT(*) AS Total FROM Orders o ',
        'JOIN Customers c ON c.Id = o.CustomerId',
        v_where
    );
    PREPARE count_stmt FROM @search_sql;
    EXECUTE count_stmt USING
        @search_name, @search_from, @search_to_exclusive, @search_min,
        @search_max, @search_status, @search_country, @search_city;
    DEALLOCATE PREPARE count_stmt;

    -- Id is a tie-breaker, so ordering and pages are stable.
    SET @search_sql = CONCAT(
        'SELECT o.Id, c.Name, c.Country, c.City, o.OrderDate, o.Amount, o.Status ',
        'FROM Orders o JOIN Customers c ON c.Id = o.CustomerId',
        v_where,
        ' ORDER BY ', v_sort, ' ', v_dir, ', o.Id ', v_dir,
        ' LIMIT ? OFFSET ?'
    );
    PREPARE page_stmt FROM @search_sql;
    EXECUTE page_stmt USING
        @search_name, @search_from, @search_to_exclusive, @search_min,
        @search_max, @search_status, @search_country, @search_city,
        @search_limit, @search_offset;
    DEALLOCATE PREPARE page_stmt;
END$$
DELIMITER ;

-- Check the actual plans for common searches with EXPLAIN ANALYZE. Indexes
-- should match real filters and sort orders; avoid adding one for every
-- possible optional-filter combination.
