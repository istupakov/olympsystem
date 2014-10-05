angular.module('OlympSystem.Admin', ['ngResource', 'ngRoute', 'ui.bootstrap']);

angular.module('OlympSystem.Admin').config(['$routeProvider', '$locationProvider',
    function ($routeProvider, $locationProvider) {
        $routeProvider
            .when('/admin', {
                templateUrl: '/admin/partial/home',
            })
            .when('/admin/news', {
                templateUrl: '/admin/partial/news',
                controller: 'SimpleCrudCtrl',
                resolve: {
                    Data: 'News'
                }
            })
            .when('/admin/news2', {
                templateUrl: '/admin/partial/news2',
                controller: 'SimpleCrudCtrl',
                resolve: {
                    Data: 'News'
                }
            })
            .when('/admin/compilators', {
                templateUrl: '/admin/partial/compilators',
                controller: 'SimpleCrudCtrl',
                resolve: {
                    Data: 'Compilator'
                }
            })
            .otherwise({
                redirectTo: '/admin'
            });
        $locationProvider.html5Mode(true);
    }]);

angular.module('OlympSystem.Admin').controller('NavbarCtrl', ['$location',
    function ($location) {
        this.navCollapsed = true;        
        this.isActive = function (viewLocation) {
            return viewLocation === $location.path();
        };
    }]);
    
angular.module('OlympSystem.Admin').controller('SimpleCrudCtrl', ['$scope', 'Data',
    function ($scope, Entry) {
        $scope.addMode = false;

        $scope.items = Entry.query();

        $scope.gridOptions = { data: 'items' };

        $scope.toggleEdit = function (index) {
            var item = $scope.items[index];
            if (item.editMode) {
                $scope.items[index] = $scope.oldValue;
            } else {
                $scope.oldValue = angular.copy(item);
                item.editMode = !item.editMode;
            }
        };

        $scope.toggleAdd = function () {
            $scope.new = new Entry();
            $scope.addMode = !$scope.addMode;
        };

        $scope.save = function (item) {
            item.$update(function () { item.editMode = false; });
        };

        $scope.add = function (item) {
            item.$save(function (data) { $scope.addMode = false; $scope.items.push(data); });
        };

        $scope.delete = function (index) {
            var item = $scope.items[index];
            item.$delete(function (data) { $scope.items.splice(index, 1); });
        };
    }]);

angular.module('OlympSystem.Admin').factory('News', ['$resource',
    function ($resource) {
        return $resource('/api/news/:Id',
            { Id: '@Id' },
            { 'update': { method: 'PUT' } }
       );
    }]);

angular.module('OlympSystem.Admin').factory('Compilator', ['$resource',
    function ($resource) {
        return $resource('/api/compilators/:Id',
            { Id: '@Id' },
            { 'update': { method: 'PUT' } }
       );
    }]);